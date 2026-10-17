using PaymentOrchestrator.Application.Outbox;

namespace PaymentOrchestrator.Application.Webhooks;

public sealed record MerchantWebhookEndpoint(string MerchantId, Uri Url, string Secret);

public interface IMerchantWebhookDirectory
{
    MerchantWebhookEndpoint? Find(string merchantId);
}

public enum WebhookDeliveryStatus
{
    Pending,
    Delivered,

    /// <summary>Gave up after the maximum number of attempts. Can be replayed manually.</summary>
    Dead,
}

public sealed record WebhookDeliveryResponse(
    Guid Id,
    Guid EventId,
    string EventType,
    string Url,
    string Status,
    int Attempts,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? DeliveredAt);

public interface IWebhookDeliveryStore
{
    /// <summary>Creates a delivery for the event. Safe to call more than once for the same event (at-least-once consumers).</summary>
    Task ScheduleAsync(IntegrationEvent integrationEvent, MerchantWebhookEndpoint endpoint, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookDeliveryResponse>> ListAsync(string merchantId, WebhookDeliveryStatus? status, int limit, CancellationToken cancellationToken);

    /// <summary>Puts a delivery back in the queue. Returns false if it does not belong to the merchant.</summary>
    Task<WebhookDeliveryResponse?> ReplayAsync(string merchantId, Guid deliveryId, CancellationToken cancellationToken);
}

/// <summary>Receives published integration events and schedules webhook deliveries for the owning merchant.</summary>
public sealed class MerchantWebhookFanout(IMerchantWebhookDirectory directory, IWebhookDeliveryStore deliveries)
{
    public async Task HandleAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (directory.Find(integrationEvent.MerchantId) is { } endpoint)
        {
            await deliveries.ScheduleAsync(integrationEvent, endpoint, cancellationToken);
        }
    }
}
