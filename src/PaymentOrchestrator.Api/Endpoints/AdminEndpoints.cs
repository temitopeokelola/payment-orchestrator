using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Application.Ledger;
using PaymentOrchestrator.Application.Reconciliation;

namespace PaymentOrchestrator.Api.Endpoints;

/// <summary>Platform operator endpoints. Require an operator API key.</summary>
internal static class AdminEndpoints
{
    /// <param name="From">Start of the period (inclusive). Defaults to 24 hours before <paramref name="To"/>.</param>
    /// <param name="To">End of the period (exclusive). Defaults to now.</param>
    public sealed record RunReconciliationRequest(string Provider, DateTimeOffset? From = null, DateTimeOffset? To = null);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin")
            .WithTags("Admin")
            .RequireAuthorization(AuthorizationPolicies.Operator);

        group.MapGet("/ledger/trial-balance", async (LedgerQueries ledger, CancellationToken ct) =>
                Results.Ok(await ledger.GetTrialBalanceAsync(ct)))
            .WithName("GetTrialBalance")
            .Produces<TrialBalanceResponse>();

        group.MapPost("/reconciliation/runs", async (RunReconciliationRequest request, ReconciliationService service, TimeProvider clock, CancellationToken ct) =>
            {
                var to = request.To ?? clock.GetUtcNow();
                var from = request.From ?? to.AddHours(-24);
                if (from >= to)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "from must be before to.");
                }

                return await service.RunAsync(request.Provider, from, to, ct) is { } run
                    ? Results.Created($"/v1/admin/reconciliation/runs/{run.Id}", run)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: $"No settlement report source for provider '{request.Provider}'.");
            })
            .WithName("RunReconciliation")
            .Produces<ReconciliationRun>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/reconciliation/runs", async (string? provider, int? limit, IReconciliationStore store, CancellationToken ct) =>
                Results.Ok(await store.ListAsync(provider, limit ?? 20, ct)))
            .WithName("ListReconciliationRuns")
            .Produces<IReadOnlyList<ReconciliationRun>>();

        group.MapGet("/reconciliation/runs/{id:guid}", async (Guid id, IReconciliationStore store, CancellationToken ct) =>
                await store.GetAsync(id, ct) is { } run ? Results.Ok(run) : Results.NotFound())
            .WithName("GetReconciliationRun")
            .Produces<ReconciliationRun>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
