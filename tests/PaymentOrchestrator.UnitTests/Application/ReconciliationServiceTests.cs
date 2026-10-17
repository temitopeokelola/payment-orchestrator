using PaymentOrchestrator.Application.Reconciliation;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.UnitTests.Fakes;

namespace PaymentOrchestrator.UnitTests.Application;

public class ReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = Now.AddHours(-1);
    private static readonly DateTimeOffset To = Now.AddHours(1);

    private readonly InMemoryPayments _payments = new();
    private readonly FakeSettlementSource _source = new("sim");
    private readonly InMemoryReconciliationStore _store = new();

    private ReconciliationService CreateService() => new([_source], _payments, _store, _payments, TimeProvider.System);

    private Payment AddPayment(long amount = 10_000)
    {
        var payment = Payment.Create("m1", new Money(amount, "NGN"), "Order", "tok", null, Now);
        _payments.Add(payment);
        return payment;
    }

    private Payment AddCaptured(long amount = 10_000, string reference = "ref_1")
    {
        var payment = AddPayment(amount);
        payment.Authorize("sim", reference, Now);
        payment.Capture(Now);
        return payment;
    }

    private void Report(Payment payment, string reference, long captured, long refunded = 0) =>
        _source.Records.Add(new SettlementRecord(reference, payment.Id, payment.Amount, captured, refunded, Now));

    private async Task<ReconciliationRun> RunAsync() => (await CreateService().RunAsync("sim", From, To, CancellationToken.None))!;

    [Fact]
    public async Task Matching_records_produce_no_items()
    {
        var payment = AddCaptured();
        Report(payment, "ref_1", captured: 10_000);

        var run = await RunAsync();

        Assert.Equal(1, run.Matched);
        Assert.Empty(run.Items);
        Assert.Single(_store.Runs);
    }

    [Fact]
    public async Task Pending_payment_charged_at_provider_is_settled()
    {
        var payment = AddPayment();
        payment.AssignProvider("sim", Now);
        Report(payment, "ref_late", captured: 10_000);

        var run = await RunAsync();

        var item = Assert.Single(run.Items);
        Assert.Equal(ReconciliationItemKind.PendingSettled, item.Kind);
        Assert.True(item.Resolved);
        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal("ref_late", payment.ProviderReference);
    }

    [Fact]
    public async Task Failed_payment_that_was_charged_is_flagged()
    {
        var payment = AddPayment();
        payment.AssignProvider("sim", Now);
        payment.Fail("all_providers_unavailable", Now);
        Report(payment, "ref_x", captured: 10_000);

        var run = await RunAsync();

        var item = Assert.Single(run.Items);
        Assert.Equal(ReconciliationItemKind.ChargedButFailed, item.Kind);
        Assert.False(item.Resolved);
    }

    [Fact]
    public async Task Refund_missing_at_provider_is_an_amount_mismatch()
    {
        var payment = AddCaptured();
        payment.Refund(new Money(4_000, "NGN"), Now);
        Report(payment, "ref_1", captured: 10_000, refunded: 0);

        var run = await RunAsync();

        var item = Assert.Single(run.Items);
        Assert.Equal(ReconciliationItemKind.AmountMismatch, item.Kind);
        Assert.Equal(4_000, item.OurRefundedMinor);
        Assert.Equal(0, item.ProviderRefundedMinor);
    }

    [Fact]
    public async Task Unknown_charge_at_provider_is_missing_internally()
    {
        _source.Records.Add(new SettlementRecord("ref_ghost", Guid.NewGuid(), new Money(500, "NGN"), 500, 0, Now));

        var run = await RunAsync();

        Assert.Equal(ReconciliationItemKind.MissingInternally, Assert.Single(run.Items).Kind);
    }

    [Fact]
    public async Task Capture_with_no_provider_record_is_missing_at_provider()
    {
        AddCaptured();

        var run = await RunAsync();

        Assert.Equal(ReconciliationItemKind.MissingAtProvider, Assert.Single(run.Items).Kind);
    }

    [Fact]
    public async Task Unknown_provider_returns_null()
    {
        Assert.Null(await CreateService().RunAsync("nope", From, To, CancellationToken.None));
    }

    private sealed class FakeSettlementSource(string provider) : ISettlementReportSource
    {
        public List<SettlementRecord> Records { get; } = [];

        public string Provider => provider;

        public Task<IReadOnlyList<SettlementRecord>> GetAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SettlementRecord>>(Records);
    }

    private sealed class InMemoryReconciliationStore : IReconciliationStore
    {
        public List<ReconciliationRun> Runs { get; } = [];

        public void Add(ReconciliationRun run) => Runs.Add(run);

        public Task<ReconciliationRun?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<ReconciliationRun>> ListAsync(string? provider, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationRun>>(Runs);
    }
}
