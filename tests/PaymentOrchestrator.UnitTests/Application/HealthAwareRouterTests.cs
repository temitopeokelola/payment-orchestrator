using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.UnitTests.Fakes;

namespace PaymentOrchestrator.UnitTests.Application;

public class HealthAwareRouterTests
{
    private static readonly FakeProvider Stripe = new("stripe", 290, separateCapture: true, "USD", "GBP");
    private static readonly FakeProvider Paystack = new("paystack", 150, separateCapture: false, "NGN", "USD");
    private static readonly FakeProvider Local = new("local", 100, separateCapture: true, "NGN");

    private readonly FakeTimeProvider _clock = new();
    private readonly ProviderHealthTracker _health;
    private readonly HealthAwareRouter _router;

    public HealthAwareRouterTests()
    {
        var options = Options.Create(new RoutingOptions { MinimumSamples = 5, FailurePenaltyBasisPoints = 2_000 });
        _health = new ProviderHealthTracker(_clock, options);
        _router = new HealthAwareRouter(new ProviderRegistry([Stripe, Paystack, Local]), _health, options);
    }

    private static string[] Names(IReadOnlyList<IPaymentProvider> providers) => [.. providers.Select(p => p.Name)];

    private void RecordCalls(string provider, int successes, int failures)
    {
        for (var i = 0; i < successes; i++)
        {
            _health.Record(provider, success: true, TimeSpan.FromMilliseconds(50));
        }

        for (var i = 0; i < failures; i++)
        {
            _health.Record(provider, success: false, TimeSpan.FromMilliseconds(50));
        }
    }

    [Fact]
    public void Filters_by_currency_and_orders_by_fee()
    {
        var ranked = _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, null));

        Assert.Equal(["paystack", "stripe"], Names(ranked));
    }

    [Fact]
    public void Excludes_providers_without_separate_capture_when_authorizing_only()
    {
        var ranked = _router.Rank(new RoutingRequest(new Money(1_000, "NGN"), RequiresSeparateCapture: true, null));

        Assert.Equal(["local"], Names(ranked));
    }

    [Fact]
    public void Preferred_provider_goes_first_but_others_remain_as_fallback()
    {
        var ranked = _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, "stripe"));

        Assert.Equal(["stripe", "paystack"], Names(ranked));
    }

    [Fact]
    public void Returns_empty_when_no_provider_supports_currency()
    {
        Assert.Empty(_router.Rank(new RoutingRequest(new Money(1_000, "JPY"), false, null)));
    }

    [Fact]
    public void Failing_cheap_provider_is_demoted_below_healthy_expensive_one()
    {
        // paystack: 150bps + 20% failures * 2000 = 550 effective, stripe stays at 290.
        RecordCalls("paystack", successes: 8, failures: 2);

        var ranked = _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, null));

        Assert.Equal(["stripe", "paystack"], Names(ranked));
    }

    [Fact]
    public void Too_few_samples_do_not_affect_ranking()
    {
        RecordCalls("paystack", successes: 0, failures: 2);

        var ranked = _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, null));

        Assert.Equal("paystack", ranked[0].Name);
    }

    [Fact]
    public void Old_failures_fall_out_of_the_window()
    {
        RecordCalls("paystack", successes: 0, failures: 10);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var ranked = _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, null));

        Assert.Equal("paystack", ranked[0].Name);
    }

    [Fact]
    public void Open_circuit_is_probed_again_once_break_has_elapsed()
    {
        _health.SetCircuit("paystack", CircuitState.Open, _clock.GetUtcNow().AddSeconds(10));
        Assert.Equal("stripe", _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, null))[0].Name);

        _clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(CircuitState.HalfOpen, _health.Snapshot("paystack").Circuit);
        Assert.Equal("paystack", _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, null))[0].Name);
    }

    [Fact]
    public void Open_circuit_goes_last_even_when_preferred()
    {
        _health.SetCircuit("stripe", CircuitState.Open, _clock.GetUtcNow().AddSeconds(30));

        var ranked = _router.Rank(new RoutingRequest(new Money(1_000, "USD"), false, "stripe"));

        Assert.Equal(["paystack", "stripe"], Names(ranked));
    }
}
