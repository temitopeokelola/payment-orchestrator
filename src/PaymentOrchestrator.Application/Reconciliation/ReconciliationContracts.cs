using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Reconciliation;

/// <summary>A provider's own record of a charge, as it appears in their settlement or balance report.</summary>
public sealed record SettlementRecord(
    string ProviderReference,
    Guid? PaymentId,
    Money Amount,
    long CapturedMinor,
    long RefundedMinor,
    DateTimeOffset CreatedAt);

/// <summary>Fetches a provider's settlement report. Implemented per provider.</summary>
public interface ISettlementReportSource
{
    string Provider { get; }

    Task<IReadOnlyList<SettlementRecord>> GetAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
}

public enum ReconciliationItemKind
{
    /// <summary>We had no outcome (provider timed out) but the provider charged; settled automatically.</summary>
    PendingSettled,

    /// <summary>The provider has a charge we have no payment for.</summary>
    MissingInternally,

    /// <summary>We recorded money moving that the provider has no record of.</summary>
    MissingAtProvider,

    /// <summary>Captured or refunded amounts differ.</summary>
    AmountMismatch,

    /// <summary>We marked the payment failed but the provider charged the customer. Needs a refund.</summary>
    ChargedButFailed,
}

public sealed record ReconciliationItem(
    ReconciliationItemKind Kind,
    Guid? PaymentId,
    string? ProviderReference,
    long? OurCapturedMinor,
    long? ProviderCapturedMinor,
    long? OurRefundedMinor,
    long? ProviderRefundedMinor,
    bool Resolved,
    string Detail);

public sealed record ReconciliationRun(
    Guid Id,
    string Provider,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int Matched,
    IReadOnlyList<ReconciliationItem> Items)
{
    public int Unresolved => Items.Count(i => !i.Resolved);
}

public interface IReconciliationStore
{
    void Add(ReconciliationRun run);

    Task<ReconciliationRun?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconciliationRun>> ListAsync(string? provider, int limit, CancellationToken cancellationToken);
}

public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    /// <summary>How often the background job runs. 0 disables it.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>How far back each scheduled run looks.</summary>
    public int LookbackHours { get; set; } = 24;

    /// <summary>Ignore the most recent minutes, where webhooks may still be in flight.</summary>
    public int SettleDelayMinutes { get; set; } = 5;
}
