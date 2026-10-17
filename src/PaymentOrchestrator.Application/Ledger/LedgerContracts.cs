using PaymentOrchestrator.Domain.Ledger;

namespace PaymentOrchestrator.Application.Ledger;

public sealed class LedgerOptions
{
    public const string SectionName = "Ledger";

    /// <summary>Fee we charge merchants on each captured payment.</summary>
    public int PlatformFeeBasisPoints { get; set; } = 200;
}

public interface ILedgerRepository
{
    void Add(JournalEntry entry);

    Task<IReadOnlyList<JournalEntry>> GetForPaymentAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountBalance>> GetBalancesAsync(string? accountPrefix, CancellationToken cancellationToken);
}

/// <summary>Net balance of an account: debits minus credits.</summary>
public sealed record AccountBalance(string Account, string Currency, long BalanceMinor);

public sealed record PostingResponse(string Account, long Amount);

public sealed record JournalEntryResponse(Guid Id, string Type, string Currency, DateTimeOffset CreatedAt, IReadOnlyList<PostingResponse> Postings)
{
    public static JournalEntryResponse From(JournalEntry entry) => new(
        entry.Id,
        entry.Type.ToString().ToLowerInvariant(),
        entry.Currency,
        entry.CreatedAt,
        [.. entry.Postings.Select(p => new PostingResponse(p.Account, p.AmountMinor))]);
}

/// <param name="Available">What we owe the merchant, in minor units, per currency.</param>
public sealed record MerchantBalanceResponse(IReadOnlyList<CurrencyBalance> Available);

public sealed record CurrencyBalance(string Currency, long Amount);

public sealed record TrialBalanceResponse(IReadOnlyList<AccountBalance> Accounts, IReadOnlyDictionary<string, long> TotalsByCurrency, bool Balanced);
