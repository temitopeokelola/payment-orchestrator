namespace PaymentOrchestrator.Application.Outbox;

/// <summary>
/// Queues an integration event to be published after the current transaction commits. Because the message is
/// stored in the same database transaction as the state change, an event is published if and only if the
/// change was saved.
/// </summary>
public interface IOutbox
{
    void Enqueue(IntegrationEvent integrationEvent);
}

/// <summary>A public, versioned event for other services and merchant webhooks.</summary>
/// <param name="Data">JSON snapshot of the resource after the change.</param>
public sealed record IntegrationEvent(Guid Id, string Type, string MerchantId, DateTimeOffset OccurredAt, string Data);

public static class IntegrationEventTypes
{
    public const string PaymentCreated = "payment.created";
    public const string PaymentAuthorized = "payment.authorized";
    public const string PaymentCaptured = "payment.captured";
    public const string PaymentRefunded = "payment.refunded";
    public const string PaymentFailed = "payment.failed";
    public const string PaymentCancelled = "payment.cancelled";
}
