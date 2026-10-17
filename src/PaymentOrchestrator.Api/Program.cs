using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Api.Endpoints;
using PaymentOrchestrator.Api.ErrorHandling;
using PaymentOrchestrator.Application;
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
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
