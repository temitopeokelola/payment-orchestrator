using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Providers.Simulator;

namespace PaymentOrchestrator.Api.Endpoints;

/// <summary>Development-only controls for the simulated providers: outages and webhook delivery.</summary>
internal static class SimulatorEndpoints
{
    public sealed record OutageRequest(bool Down);

    public static IEndpointRouteBuilder MapSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dev/simulators/{name}").WithTags("Simulator (development)");

        group.MapPost("/outage", (string name, OutageRequest request, SimulatorLedger ledger, IEnumerable<SimulatorProvider> simulators) =>
        {
            if (!simulators.Any(s => s.Name == name))
            {
                return Results.NotFound();
            }

            ledger.SetOutage(name, request.Down);
            return Results.Ok(new { provider = name, down = request.Down });
        });

        // Sends the webhook the provider would send for a payment, through the real webhook pipeline.
        group.MapPost("/payments/{paymentId:guid}/webhooks/{type}", async (
            string name,
            Guid paymentId,
            string type,
            SimulatorLedger ledger,
            IEnumerable<SimulatorProvider> simulators,
            ProviderWebhookService webhooks,
            CancellationToken ct) =>
        {
            var simulator = simulators.FirstOrDefault(s => s.Name == name);
            var transaction = simulator is null ? null : ledger.Transactions(name).FirstOrDefault(t => t.PaymentId == paymentId);
            if (simulator is null || transaction is null)
            {
                return Results.NotFound();
            }

            var (body, signature) = simulator.CreateWebhook(type, transaction);
            var headers = new Dictionary<string, string> { [SimulatorProvider.SignatureHeader] = signature };
            var result = await webhooks.HandleAsync(name, headers, body, ct);

            return Results.Ok(new { result = result.ToString().ToLowerInvariant() });
        });

        return app;
    }
}
