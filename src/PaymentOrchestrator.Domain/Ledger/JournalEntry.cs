using PaymentOrchestrator.Domain.Common;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Domain.Ledger;

/// <summary>
/// One line of a journal entry. Positive amounts are debits, negative amounts are credits.
/// </summary>
public sealed record Posting(string Account, long AmountMinor);

public enum JournalEntryType
{
    Capture,
    Refund,
}

/// <summary>
/// An immutable, balanced set of postings. Every entry's postings sum to zero, so the ledger as a whole
/// always balances and money can only move between accounts, never appear or disappear.
/// </summary>
public sealed class JournalEntry
{
    private readonly List<Posting> _postings = [];

    // Required by EF Core.
    private JournalEntry()
    {
        Currency = null!;
    }

    private JournalEntry(Guid id, Guid paymentId, JournalEntryType type, string currency, DateTimeOffset createdAt, IEnumerable<Posting> postings)
    {
        Id = id;
        PaymentId = paymentId;
        Type = type;
        Currency = currency;
        CreatedAt = createdAt;
        _postings.AddRange(postings);
    }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public JournalEntryType Type { get; private set; }
    public string Currency { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<Posting> Postings => _postings;

    public static JournalEntry Create(Guid paymentId, JournalEntryType type, string currency, DateTimeOffset now, params Posting[] postings)
    {
        var lines = postings.Where(p => p.AmountMinor != 0).ToArray();

        if (lines.Length < 2)
        {
            throw new DomainException("ledger.too_few_postings", "A journal entry needs at least two non-zero postings.");
        }

        if (lines.Sum(p => p.AmountMinor) != 0)
        {
            throw new DomainException("ledger.unbalanced", "Debits and credits must balance.");
        }

        if (lines.Any(p => string.IsNullOrWhiteSpace(p.Account)))
        {
            throw new DomainException("ledger.account_required", "Every posting needs an account.");
        }

        return new JournalEntry(Guid.CreateVersion7(now), paymentId, type, currency, now, lines);
    }

    /// <summary>
    /// Customer pays <paramref name="amount"/>. The provider keeps its processing fee, we keep our platform fee,
    /// and the rest is owed to the merchant.
    /// </summary>
    public static JournalEntry ForCapture(
        Guid paymentId,
        string merchantId,
        string provider,
        Money amount,
        int providerFeeBasisPoints,
        int platformFeeBasisPoints,
        DateTimeOffset now)
    {
        var providerFee = Fees.Calculate(amount.AmountMinor, providerFeeBasisPoints);
        var platformFee = Fees.Calculate(amount.AmountMinor, platformFeeBasisPoints);

        return Create(paymentId, JournalEntryType.Capture, amount.Currency, now,
            new Posting(LedgerAccounts.ProviderClearing(provider), amount.AmountMinor - providerFee),
            new Posting(LedgerAccounts.ProcessingFees, providerFee),
            new Posting(LedgerAccounts.MerchantPayable(merchantId), -(amount.AmountMinor - platformFee)),
            new Posting(LedgerAccounts.PlatformRevenue, -platformFee));
    }

    /// <summary>Refunds come out of the merchant's balance and go back through the provider. Fees are not returned.</summary>
    public static JournalEntry ForRefund(Guid paymentId, string merchantId, string provider, Money amount, DateTimeOffset now) =>
        Create(paymentId, JournalEntryType.Refund, amount.Currency, now,
            new Posting(LedgerAccounts.MerchantPayable(merchantId), amount.AmountMinor),
            new Posting(LedgerAccounts.ProviderClearing(provider), -amount.AmountMinor));
}

public static class LedgerAccounts
{
    /// <summary>Asset: money the provider has collected for us but not yet paid out.</summary>
    public static string ProviderClearing(string provider) => $"provider:{provider}:clearing";

    /// <summary>Liability: money we owe the merchant.</summary>
    public static string MerchantPayable(string merchantId) => $"merchant:{merchantId}:payable";

    /// <summary>Expense: fees charged by providers.</summary>
    public const string ProcessingFees = "platform:processing_fees";

    /// <summary>Revenue: fees we charge merchants.</summary>
    public const string PlatformRevenue = "platform:revenue";
}

public static class Fees
{
    /// <summary>Basis-point fee rounded half away from zero, in integer arithmetic.</summary>
    public static long Calculate(long amountMinor, int basisPoints) =>
        ((amountMinor * basisPoints) + 5_000) / 10_000;
}
