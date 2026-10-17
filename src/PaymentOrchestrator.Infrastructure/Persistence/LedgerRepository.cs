using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Ledger;
using PaymentOrchestrator.Domain.Ledger;

namespace PaymentOrchestrator.Infrastructure.Persistence;

internal sealed class LedgerRepository(PaymentsDbContext db) : ILedgerRepository
{
    public void Add(JournalEntry entry) => db.JournalEntries.Add(entry);

    public async Task<IReadOnlyList<JournalEntry>> GetForPaymentAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await db.JournalEntries
            .AsNoTracking()
            .Where(e => e.PaymentId == paymentId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AccountBalance>> GetBalancesAsync(string? accountPrefix, CancellationToken cancellationToken)
    {
        var postings = db.JournalEntries
            .AsNoTracking()
            .SelectMany(e => e.Postings, (e, p) => new { p.Account, e.Currency, p.AmountMinor });

        if (accountPrefix is not null)
        {
            postings = postings.Where(p => p.Account.StartsWith(accountPrefix));
        }

        var rows = await postings
            .GroupBy(p => new { p.Account, p.Currency })
            .Select(g => new { g.Key.Account, g.Key.Currency, Balance = g.Sum(p => p.AmountMinor) })
            .OrderBy(r => r.Account)
            .ThenBy(r => r.Currency)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => new AccountBalance(r.Account, r.Currency, r.Balance))];
    }
}
