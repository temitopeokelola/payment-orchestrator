using PaymentOrchestrator.Domain.Common;
using PaymentOrchestrator.Domain.Ledger;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.UnitTests.Domain;

public class JournalEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Capture_entry_balances_and_splits_fees()
    {
        // 10,000.00 NGN, provider takes 1.4%, platform charges 2%.
        var entry = JournalEntry.ForCapture(Guid.NewGuid(), "m1", "simulator-a", new Money(1_000_000, "NGN"), 140, 200, Now);

        Assert.Equal(0, entry.Postings.Sum(p => p.AmountMinor));
        Assert.Contains(new Posting("provider:simulator-a:clearing", 986_000), entry.Postings);
        Assert.Contains(new Posting("platform:processing_fees", 14_000), entry.Postings);
        Assert.Contains(new Posting("merchant:m1:payable", -980_000), entry.Postings);
        Assert.Contains(new Posting("platform:revenue", -20_000), entry.Postings);
    }

    [Fact]
    public void Refund_entry_reverses_merchant_balance()
    {
        var entry = JournalEntry.ForRefund(Guid.NewGuid(), "m1", "simulator-a", new Money(4_000, "NGN"), Now);

        Assert.Equal(0, entry.Postings.Sum(p => p.AmountMinor));
        Assert.Contains(new Posting("merchant:m1:payable", 4_000), entry.Postings);
        Assert.Contains(new Posting("provider:simulator-a:clearing", -4_000), entry.Postings);
    }

    [Fact]
    public void Unbalanced_entry_is_rejected()
    {
        var ex = Assert.Throws<DomainException>(() =>
            JournalEntry.Create(Guid.NewGuid(), JournalEntryType.Capture, "NGN", Now, new Posting("a", 100), new Posting("b", -99)));

        Assert.Equal("ledger.unbalanced", ex.Code);
    }

    [Fact]
    public void Zero_postings_are_dropped()
    {
        // A zero platform fee should not produce a zero line.
        var entry = JournalEntry.ForCapture(Guid.NewGuid(), "m1", "p", new Money(10_000, "NGN"), 100, 0, Now);

        Assert.DoesNotContain(entry.Postings, p => p.AmountMinor == 0);
        Assert.Equal(3, entry.Postings.Count);
    }

    [Fact]
    public void Single_posting_is_rejected()
    {
        Assert.Throws<DomainException>(() =>
            JournalEntry.Create(Guid.NewGuid(), JournalEntryType.Capture, "NGN", Now, new Posting("a", 0), new Posting("b", 0)));
    }

    [Theory]
    [InlineData(10_000, 150, 150)]
    [InlineData(333, 150, 5)] // 4.995 rounds up
    [InlineData(100, 140, 1)] // 1.4 rounds down
    [InlineData(0, 150, 0)]
    public void Fees_round_half_away_from_zero(long amount, int basisPoints, long expected)
    {
        Assert.Equal(expected, Fees.Calculate(amount, basisPoints));
    }
}
