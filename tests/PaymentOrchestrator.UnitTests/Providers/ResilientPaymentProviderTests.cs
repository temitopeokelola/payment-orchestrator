using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.Infrastructure.Providers;
using PaymentOrchestrator.UnitTests.Fakes;

namespace PaymentOrchestrator.UnitTests.Providers;

public class ResilientPaymentProviderTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeProvider _inner = new("flaky");
    private readonly ProviderHealthTracker _health;
    private readonly ResilientPaymentProvider _provider;

    public ResilientPaymentProviderTests()
    {
        var options = new RoutingOptions { CircuitMinimumThroughput = 4, CircuitFailureRatio = 0.5, CircuitSamplingSeconds = 30, CircuitBreakSeconds = 10 };
        _health = new ProviderHealthTracker(_clock, Options.Create(options));
        _provider = new ResilientPaymentProvider(_inner, _health, options, _clock);
    }

    private static AuthorizeCommand Command() => new(Guid.NewGuid(), new Money(1_000, "NGN"), "tok", null, "Order", true);

    private async Task FailAsync(int times)
    {
        for (var i = 0; i < times; i++)
        {
            _inner.AuthorizeResults.Enqueue(ProviderResult.Unavailable("provider_error"));
            await _provider.AuthorizeAsync(Command(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_fails_fast()
    {
        await FailAsync(4);
        var callsBefore = _inner.AuthorizeCalls;

        var result = await _provider.AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Unavailable, result.Outcome);
        Assert.Equal("circuit_open", result.Code);
        Assert.Equal(callsBefore, _inner.AuthorizeCalls);
        Assert.Equal(CircuitState.Open, _health.Snapshot("flaky").Circuit);
    }

    [Fact]
    public async Task Circuit_closes_again_after_a_successful_trial_call()
    {
        await FailAsync(4);
        _clock.Advance(TimeSpan.FromSeconds(11));

        var trial = await _provider.AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Approved, trial.Outcome);
        Assert.Equal(CircuitState.Closed, _health.Snapshot("flaky").Circuit);
    }

    [Fact]
    public async Task Declines_count_as_healthy_responses()
    {
        for (var i = 0; i < 6; i++)
        {
            _inner.AuthorizeResults.Enqueue(ProviderResult.Declined("card_declined"));
            await _provider.AuthorizeAsync(Command(), CancellationToken.None);
        }

        var health = _health.Snapshot("flaky");
        Assert.Equal(1.0, health.SuccessRate);
        Assert.Equal(CircuitState.Closed, health.Circuit);
    }
}
