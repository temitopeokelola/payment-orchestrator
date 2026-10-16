using Microsoft.AspNetCore.Authentication;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Api.Endpoints;
using PaymentOrchestrator.Api.ErrorHandling;
using PaymentOrchestrator.Application;
using PaymentOrchestrator.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.Configure<MerchantOptions>(builder.Configuration.GetSection(MerchantOptions.SectionName));
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

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
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapPaymentEndpoints();

app.Run();

public partial class Program;
