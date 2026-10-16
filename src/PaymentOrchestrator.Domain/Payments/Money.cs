using PaymentOrchestrator.Domain.Common;

namespace PaymentOrchestrator.Domain.Payments;

/// <summary>
/// An amount in the currency's minor unit (kobo, cents, pence) to avoid floating point errors.
/// </summary>
public readonly record struct Money
{
    public long AmountMinor { get; }
    public string Currency { get; }

    public Money(long amountMinor, string currency)
    {
        if (amountMinor < 0)
        {
            throw new DomainException("money.negative", "Amount cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new DomainException("money.invalid_currency", "Currency must be a 3-letter ISO 4217 code.");
        }

        AmountMinor = amountMinor;
        Currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency) => new(0, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(AmountMinor + other.AmountMinor), Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(AmountMinor - other.AmountMinor, Currency);
    }

    public bool IsGreaterThan(Money other)
    {
        EnsureSameCurrency(other);
        return AmountMinor > other.AmountMinor;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (other.Currency != Currency)
        {
            throw new DomainException("money.currency_mismatch", $"Cannot combine {Currency} with {other.Currency}.");
        }
    }

    public override string ToString() => $"{AmountMinor} {Currency}";
}
