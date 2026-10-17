using System.Security.Claims;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Application.Ledger;

namespace PaymentOrchestrator.Api.Endpoints;

internal static class LedgerEndpoints
{
    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1").WithTags("Ledger").RequireAuthorization();

        group.MapGet("/balance", async (ClaimsPrincipal user, LedgerQueries ledger, CancellationToken ct) =>
                Results.Ok(await ledger.GetMerchantBalanceAsync(user.GetMerchantId(), ct)))
            .WithName("GetBalance")
            .Produces<MerchantBalanceResponse>();

        group.MapGet("/payments/{id:guid}/ledger", async (Guid id, ClaimsPrincipal user, LedgerQueries ledger, CancellationToken ct) =>
                await ledger.GetPaymentEntriesAsync(user.GetMerchantId(), id, ct) is { } entries
                    ? Results.Ok(entries)
                    : Results.NotFound())
            .WithName("GetPaymentLedgerEntries")
            .Produces<IReadOnlyList<JournalEntryResponse>>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
