using PaymentOrchestrator.Domain.Common;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.UnitTests.Domain;

public class MoneyTests
{
    [Fact]
    public void Currency_is_normalised_to_upper_case()
    {
        var money = new Money(1000, "ngn");

        Assert.Equal("NGN", money.Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NG")]
    [InlineData("NGNX")]
    [InlineData("N1N")]
    public void Invalid_currency_is_rejected(string currency)
    {
        var ex = Assert.Throws<DomainException>(() => new Money(100, currency));

        Assert.Equal("money.invalid_currency", ex.Code);
    }

    [Fact]
    public void Negative_amount_is_rejected()
    {
        var ex = Assert.Throws<DomainException>(() => new Money(-1, "USD"));

        Assert.Equal("money.negative", ex.Code);
    }

    [Fact]
    public void Adding_different_currencies_is_rejected()
    {
        var naira = new Money(100, "NGN");
        var dollars = new Money(100, "USD");

        var ex = Assert.Throws<DomainException>(() => naira.Add(dollars));

        Assert.Equal("money.currency_mismatch", ex.Code);
    }

    [Fact]
    public void Values_with_same_amount_and_currency_are_equal()
    {
        Assert.Equal(new Money(500, "GBP"), new Money(500, "gbp"));
    }
}
