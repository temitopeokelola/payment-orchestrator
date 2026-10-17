using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Ledger;

namespace PaymentOrchestrator.Application.Ledger;

public sealed class LedgerQueries(ILedgerRepository ledger, IPaymentRepository payments)
{
    public async Task<IReadOnlyList<JournalEntryResponse>?> GetPaymentEntriesAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (await payments.GetAsync(paymentId, merchantId, cancellationToken) is null)
        {
            return null;
        }

        var entries = await ledger.GetForPaymentAsync(paymentId, cancellationToken);
        return [.. entries.Select(JournalEntryResponse.From)];
    }

    public async Task<MerchantBalanceResponse> GetMerchantBalanceAsync(string merchantId, CancellationToken cancellationToken)
    {
        var balances = await ledger.GetBalancesAsync(LedgerAccounts.MerchantPayable(merchantId), cancellationToken);

        // A liability carries a credit (negative) balance; flip the sign to show what the merchant is owed.
        return new MerchantBalanceResponse([.. balances.Select(b => new CurrencyBalance(b.Currency, -b.BalanceMinor))]);
    }

    public async Task<TrialBalanceResponse> GetTrialBalanceAsync(CancellationToken cancellationToken)
    {
        var balances = await ledger.GetBalancesAsync(null, cancellationToken);
        var totals = balances.GroupBy(b => b.Currency).ToDictionary(g => g.Key, g => g.Sum(b => b.BalanceMinor));
        return new TrialBalanceResponse(balances, totals, totals.Values.All(t => t == 0));
    }
}
