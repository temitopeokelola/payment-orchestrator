using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Api.Endpoints;
using PaymentOrchestrator.Api.ErrorHandling;
using PaymentOrchestrator.Application;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Infrastructure;
using PaymentOrchestrator.Infrastructure.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.Configure<MerchantOptions>(builder.Configuration.GetSection(MerchantOptions.SectionName));
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.Configure<OperatorOptions>(builder.Configuration.GetSection(OperatorOptions.SectionName));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthorizationPolicies.Merchant, p => p.RequireClaim(ApiKeyAuthenticationHandler.MerchantIdClaim))
    .AddPolicy(AuthorizationPolicies.Operator, p => p.RequireRole(ApiKeyAuthenticationHandler.OperatorRole))
    // Endpoints that just say RequireAuthorization() are merchant endpoints.
    .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireClaim(ApiKeyAuthenticationHandler.MerchantIdClaim).Build());

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentsDbContext>("database", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("payment-orchestrator"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(Telemetry.Name, "RabbitMQ.Client.Publisher", "RabbitMQ.Client.Subscriber")
            .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/metrics") && !ctx.Request.Path.StartsWithSegments("/health"))
            .AddHttpClientInstrumentation()
            .AddNpgsql();

        // Export traces when a collector is configured (Jaeger in docker compose).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics => metrics
        .AddMeter(Telemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

// Lets the idempotency filter hash the raw body after model binding has read it.
app.Use((context, next) =>
{
    context.Request.EnableBuffering();
    return next(context);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapPrometheusScrapingEndpoint();

InfrastructureTelemetry.RegisterProviderGauges(
    app.Services.GetRequiredService<IProviderRegistry>(),
    app.Services.GetRequiredService<IProviderHealth>());
app.MapPaymentEndpoints();
app.MapLedgerEndpoints();
app.MapProviderEndpoints();
app.MapWebhookDeliveryEndpoints();
app.MapProviderWebhookEndpoints();
app.MapAdminEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapSimulatorEndpoints();
    app.MapDevWebhookReceiver();
}

app.Run();

public partial class Program;
