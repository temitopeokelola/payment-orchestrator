using Microsoft.Extensions.Logging;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Webhooks;

public interface IProviderWebhookInbox
{
    /// <summary>Records the event. Returns false if this provider event was already received.</summary>
    Task<bool> TryRecordAsync(string provider, ProviderWebhookEvent webhookEvent, string payload, CancellationToken cancellationToken);
}

public enum WebhookHandlingResult
{
    Applied,
    Duplicate,
    Ignored,
    UnknownProvider,
}

/// <summary>
/// Applies provider webhooks to payments. Webhooks are how we learn the outcome of a timed-out authorization
/// and of asynchronous events such as refunds settling.
/// </summary>
public sealed partial class ProviderWebhookService(
    IProviderRegistry providers,
    IProviderWebhookInbox inbox,
    IPaymentRepository payments,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ProviderWebhookService> logger)
{
    public async Task<WebhookHandlingResult> HandleAsync(
        string providerName,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellationToken)
    {
        var provider = providers.Find(providerName);
        if (provider is null)
        {
            return WebhookHandlingResult.UnknownProvider;
        }

        // Throws WebhookSignatureException for forged or tampered requests.
        var webhookEvent = provider.ParseWebhook(headers, body);

        var result = WebhookHandlingResult.Ignored;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Recording and applying share a transaction: if applying fails, the record rolls back and the
            // provider's retry is processed instead of being dropped as a duplicate.
            if (!await inbox.TryRecordAsync(provider.Name, webhookEvent, body, ct))
            {
                result = WebhookHandlingResult.Duplicate;
                return;
            }

            if (webhookEvent.Type == ProviderWebhookEventType.Ignored || webhookEvent.PaymentId is not { } paymentId)
            {
                return;
            }

            var payment = await payments.FindAsync(paymentId, ct);
            if (payment is null)
            {
                LogUnknownPayment(provider.Name, webhookEvent.EventId, paymentId);
                return;
            }

            if (Apply(payment, provider.Name, webhookEvent))
            {
                result = WebhookHandlingResult.Applied;
            }

            await unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

        return result;
    }

    private bool Apply(Payment payment, string provider, ProviderWebhookEvent webhookEvent)
    {
        var now = clock.GetUtcNow();
        var reference = webhookEvent.ProviderReference ?? payment.ProviderReference;

        switch (webhookEvent.Type)
        {
            case ProviderWebhookEventType.PaymentAuthorized when payment.Status == PaymentStatus.Created && reference is not null:
                payment.Authorize(provider, reference, now);
                return true;

            case ProviderWebhookEventType.PaymentCaptured when payment.Status == PaymentStatus.Created && reference is not null:
                payment.Authorize(provider, reference, now);
                payment.Capture(now);
                return true;

            case ProviderWebhookEventType.PaymentCaptured when payment.Status == PaymentStatus.Authorized:
                payment.Capture(now);
                return true;

            case ProviderWebhookEventType.PaymentFailed when payment.Status == PaymentStatus.Created:
                payment.Fail(webhookEvent.FailureCode ?? "provider_failed", now);
                return true;

            default:
                // Events that describe a state we already reached (or moved past) are expected and harmless.
                return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {EventId} from {Provider} refers to unknown payment {PaymentId}")]
    private partial void LogUnknownPayment(string provider, string eventId, Guid paymentId);
}
