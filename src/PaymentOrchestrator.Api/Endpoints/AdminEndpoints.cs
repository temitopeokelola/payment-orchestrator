using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Application.Ledger;

namespace PaymentOrchestrator.Api.Endpoints;

/// <summary>Platform operator endpoints. Require an operator API key.</summary>
internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin")
            .WithTags("Admin")
            .RequireAuthorization(AuthorizationPolicies.Operator);

        group.MapGet("/ledger/trial-balance", async (LedgerQueries ledger, CancellationToken ct) =>
                Results.Ok(await ledger.GetTrialBalanceAsync(ct)))
            .WithName("GetTrialBalance")
            .Produces<TrialBalanceResponse>();

        return app;
    }
}
