using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.UnitTests.Fakes;

namespace PaymentOrchestrator.UnitTests.Application;

public class CapabilityRouterTests
{
    private static readonly FakeProvider Stripe = new("stripe", 290, separateCapture: true, "USD", "GBP");
    private static readonly FakeProvider Paystack = new("paystack", 150, separateCapture: false, "NGN", "USD");
    private static readonly FakeProvider Local = new("local", 100, separateCapture: true, "NGN");

    private static readonly CapabilityRouter Router = new(new ProviderRegistry([Stripe, Paystack, Local]));

    private static string[] Names(IReadOnlyList<IPaymentProvider> providers) => [.. providers.Select(p => p.Name)];

    [Fact]
    public void Filters_by_currency_and_orders_by_fee()
    {
        var ranked = Router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, null));

        Assert.Equal(["paystack", "stripe"], Names(ranked));
    }

    [Fact]
    public void Excludes_providers_without_separate_capture_when_authorizing_only()
    {
        var ranked = Router.Rank(new RoutingRequest(new Money(1_000, "NGN"), RequiresSeparateCapture: true, null));

        Assert.Equal(["local"], Names(ranked));
    }

    [Fact]
    public void Preferred_provider_goes_first_but_others_remain_as_fallback()
    {
        var ranked = Router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, "stripe"));

        Assert.Equal(["stripe", "paystack"], Names(ranked));
    }

    [Fact]
    public void Returns_empty_when_no_provider_supports_currency()
    {
        Assert.Empty(Router.Rank(new RoutingRequest(new Money(1_000, "JPY"), false, null)));
    }
}
