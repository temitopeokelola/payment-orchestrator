using System.Security.Claims;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;

namespace PaymentOrchestrator.Api.Endpoints;

internal static class ProviderEndpoints
{
    public sealed record ProviderStatusResponse(
        string Name,
        IReadOnlyList<string> Currencies,
        int FeeBasisPoints,
        bool SupportsSeparateCapture,
        string Circuit,
        int RecentCalls,
        double SuccessRate,
        double AverageLatencyMs);

    public static IEndpointRouteBuilder MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/providers", (IProviderRegistry registry, IProviderHealth health) =>
                Results.Ok(registry.All
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p =>
                    {
                        var snapshot = health.Snapshot(p.Name);
                        return new ProviderStatusResponse(
                            p.Name,
                            [.. p.Capabilities.Currencies.Order(StringComparer.Ordinal)],
                            p.Capabilities.FeeBasisPoints,
                            p.Capabilities.SupportsSeparateCapture,
                            snapshot.Circuit.ToString().ToLowerInvariant(),
                            snapshot.Samples,
                            Math.Round(snapshot.SuccessRate, 4),
                            Math.Round(snapshot.AverageLatencyMs, 1));
                    })))
            .WithTags("Providers")
            .WithName("ListProviders")
            .RequireAuthorization()
            .Produces<IEnumerable<ProviderStatusResponse>>();

        app.MapGet("/v1/payments/{id:guid}/attempts", async (Guid id, ClaimsPrincipal user, PaymentService service, CancellationToken ct) =>
                await service.GetAttemptsAsync(user.GetMerchantId(), id, ct) is { } attempts ? Results.Ok(attempts) : Results.NotFound())
            .WithTags("Payments")
            .WithName("ListPaymentAttempts")
            .RequireAuthorization()
            .Produces<IReadOnlyList<PaymentAttempt>>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
