using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.UnitTests.Fakes;

namespace PaymentOrchestrator.UnitTests.Application;

public class PaymentServiceTests
{
    private readonly InMemoryPayments _store = new();
    private readonly FakeProvider _cheap = new("cheap", feeBasisPoints: 100);
    private readonly FakeProvider _backup = new("backup", feeBasisPoints: 200);
    private readonly InMemoryAttemptLog _attempts = new();

    private PaymentService CreateService()
    {
        var registry = new ProviderRegistry([_cheap, _backup]);
        var options = Options.Create(new RoutingOptions());
        var router = new HealthAwareRouter(registry, new ProviderHealthTracker(TimeProvider.System, options), options);
        return new PaymentService(_store, _store, router, registry, _attempts, TimeProvider.System, NullLogger<PaymentService>.Instance);
    }

    private static CreatePaymentRequest Request(bool capture = true) =>
        new(10_000, "NGN", "Order", "tok_test", "ada@example.com", capture);

    [Fact]
    public async Task Uses_cheapest_provider_first()
    {
        var payment = await CreateService().CreateAsync("m1", Request(), CancellationToken.None);

        Assert.Equal("captured", payment.Status);
        Assert.Equal("cheap", payment.Provider);
        Assert.Equal(0, _backup.AuthorizeCalls);
    }

    [Fact]
    public async Task Fails_over_when_provider_is_unavailable()
    {
        _cheap.AuthorizeResults.Enqueue(ProviderResult.Unavailable("provider_outage"));

        var payment = await CreateService().CreateAsync("m1", Request(), CancellationToken.None);

        Assert.Equal("captured", payment.Status);
        Assert.Equal("backup", payment.Provider);
    }

    [Fact]
    public async Task Does_not_fail_over_on_unknown_outcome()
    {
        _cheap.AuthorizeResults.Enqueue(ProviderResult.Unknown("timeout"));

        var payment = await CreateService().CreateAsync("m1", Request(), CancellationToken.None);

        Assert.Equal("created", payment.Status);
        Assert.Equal("cheap", payment.Provider);
        Assert.Equal(0, _backup.AuthorizeCalls);
    }

    [Fact]
    public async Task Records_every_provider_attempt()
    {
        _cheap.AuthorizeResults.Enqueue(ProviderResult.Unavailable("provider_outage"));

        var payment = await CreateService().CreateAsync("m1", Request(capture: false), CancellationToken.None);

        Assert.Equal(
            [("cheap", "authorize", "unavailable"), ("backup", "authorize", "approved")],
            _attempts.Attempts.Select(a => (a.Provider, a.Operation, a.Outcome)));
        Assert.All(_attempts.Attempts, a => Assert.Equal(payment.Id, a.PaymentId));
    }

    [Fact]
    public async Task Does_not_fail_over_on_decline()
    {
        _cheap.AuthorizeResults.Enqueue(ProviderResult.Declined("insufficient_funds"));

        var payment = await CreateService().CreateAsync("m1", Request(), CancellationToken.None);

        Assert.Equal("failed", payment.Status);
        Assert.Equal("insufficient_funds", payment.FailureReason);
        Assert.Equal(0, _backup.AuthorizeCalls);
    }

    [Fact]
    public async Task Persists_payment_before_calling_provider()
    {
        _cheap.AuthorizeResults.Enqueue(ProviderResult.Unknown("timeout"));

        await CreateService().CreateAsync("m1", Request(), CancellationToken.None);

        Assert.Equal(2, _store.SaveCount);
    }

    [Fact]
    public async Task Refund_calls_provider_and_updates_payment()
    {
        var service = CreateService();
        var payment = await service.CreateAsync("m1", Request(), CancellationToken.None);

        var refunded = await service.RefundAsync("m1", payment.Id, new RefundPaymentRequest(2_500), CancellationToken.None);

        Assert.Equal("partially_refunded", refunded!.Status);
        Assert.Equal(2_500, refunded.RefundedAmount);
    }
}
