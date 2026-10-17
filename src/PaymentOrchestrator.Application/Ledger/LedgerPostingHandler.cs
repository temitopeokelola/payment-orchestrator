using Microsoft.Extensions.Options;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Ledger;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Ledger;

/// <summary>Writes journal entries whenever money actually moves.</summary>
public sealed class LedgerPostingHandler(
    ILedgerRepository ledger,
    IProviderRegistry providers,
    IOptions<LedgerOptions> options)
    : IDomainEventSubscriber<PaymentCaptured>, IDomainEventSubscriber<PaymentRefunded>
{
    public Task HandleAsync(PaymentCaptured domainEvent, CancellationToken cancellationToken)
    {
        // Fee rates are taken at posting time and frozen into the entry, so later rate changes don't rewrite history.
        var providerFee = providers.Find(domainEvent.Provider)?.Capabilities.FeeBasisPoints ?? 0;

        Telemetry.LedgerEntries.Add(1, new KeyValuePair<string, object?>("type", "capture"));
        ledger.Add(JournalEntry.ForCapture(
            domainEvent.PaymentId,
            domainEvent.MerchantId,
            domainEvent.Provider,
            domainEvent.Amount,
            providerFee,
            options.Value.PlatformFeeBasisPoints,
            domainEvent.OccurredAt));

        return Task.CompletedTask;
    }

    public Task HandleAsync(PaymentRefunded domainEvent, CancellationToken cancellationToken)
    {
        Telemetry.LedgerEntries.Add(1, new KeyValuePair<string, object?>("type", "refund"));
        ledger.Add(JournalEntry.ForRefund(
            domainEvent.PaymentId,
            domainEvent.MerchantId,
            domainEvent.Provider,
            domainEvent.Amount,
            domainEvent.OccurredAt));

        return Task.CompletedTask;
    }
}
