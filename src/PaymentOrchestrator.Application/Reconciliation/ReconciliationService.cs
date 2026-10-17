using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Reconciliation;

/// <summary>
/// Compares a provider's settlement report with our payments for the same period. Differences become items on
/// the run; payments whose outcome we never learned (timeouts) are settled from the provider's record.
/// </summary>
public sealed class ReconciliationService(
    IEnumerable<ISettlementReportSource> sources,
    IPaymentRepository payments,
    IReconciliationStore store,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public IReadOnlyList<string> Providers => [.. sources.Select(s => s.Provider).Order(StringComparer.Ordinal)];

    public async Task<ReconciliationRun?> RunAsync(string provider, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var source = sources.FirstOrDefault(s => string.Equals(s.Provider, provider, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return null;
        }

        var startedAt = clock.GetUtcNow();
        var report = await source.GetAsync(from, to, cancellationToken);
        var ours = await payments.ListForProviderAsync(source.Provider, from, to, cancellationToken);

        var oursById = ours.ToDictionary(p => p.Id);
        var seen = new HashSet<Guid>();
        var items = new List<ReconciliationItem>();
        var matched = 0;

        foreach (var record in report)
        {
            var payment = record.PaymentId is { } id
                ? oursById.GetValueOrDefault(id) ?? await payments.FindAsync(id, cancellationToken)
                : ours.FirstOrDefault(p => p.ProviderReference == record.ProviderReference);

            if (payment is null)
            {
                items.Add(Item(ReconciliationItemKind.MissingInternally, null, record, null, false, "Provider has a charge with no matching payment."));
                continue;
            }

            seen.Add(payment.Id);

            if (payment.Status == PaymentStatus.Created && record.CapturedMinor > 0)
            {
                payment.Authorize(source.Provider, record.ProviderReference, clock.GetUtcNow());
                payment.Capture(clock.GetUtcNow());
                items.Add(Item(ReconciliationItemKind.PendingSettled, payment, record, 0, true, "Outcome was unknown; captured from the provider's record."));
                continue;
            }

            if (payment.Status == PaymentStatus.Failed && record.CapturedMinor > 0)
            {
                items.Add(Item(ReconciliationItemKind.ChargedButFailed, payment, record, 0, false, "Customer was charged for a payment we marked failed. Refund required."));
                continue;
            }

            if (payment.CapturedAmount.AmountMinor != record.CapturedMinor || payment.RefundedAmount.AmountMinor != record.RefundedMinor)
            {
                items.Add(Item(ReconciliationItemKind.AmountMismatch, payment, record, payment.CapturedAmount.AmountMinor, false, "Captured or refunded amounts differ."));
                continue;
            }

            matched++;
        }

        foreach (var payment in ours.Where(p => !seen.Contains(p.Id) && p.CapturedAmount.AmountMinor > 0))
        {
            items.Add(new ReconciliationItem(
                ReconciliationItemKind.MissingAtProvider,
                payment.Id,
                payment.ProviderReference,
                payment.CapturedAmount.AmountMinor,
                null,
                payment.RefundedAmount.AmountMinor,
                null,
                Resolved: false,
                "We recorded a capture the provider has no record of."));
        }

        foreach (var item in items)
        {
            Telemetry.ReconciliationDifferences.Add(
                1,
                new KeyValuePair<string, object?>("provider", source.Provider),
                new KeyValuePair<string, object?>("kind", item.Kind.ToString()),
                new KeyValuePair<string, object?>("resolved", item.Resolved));
        }

        var run = new ReconciliationRun(Guid.CreateVersion7(startedAt), source.Provider, from, to, startedAt, clock.GetUtcNow(), matched, items);
        store.Add(run);

        // Settled payments, their ledger entries and the run commit together.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return run;
    }

    private static ReconciliationItem Item(
        ReconciliationItemKind kind,
        Payment? payment,
        SettlementRecord record,
        long? ourCaptured,
        bool resolved,
        string detail) =>
        new(
            kind,
            payment?.Id ?? record.PaymentId,
            record.ProviderReference,
            ourCaptured,
            record.CapturedMinor,
            payment?.RefundedAmount.AmountMinor,
            record.RefundedMinor,
            resolved,
            detail);
}
