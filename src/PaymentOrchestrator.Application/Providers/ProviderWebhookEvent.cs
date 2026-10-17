using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Providers;

public enum ProviderWebhookEventType
{
    Ignored,
    PaymentAuthorized,
    PaymentCaptured,
    PaymentFailed,
    RefundSucceeded,
}

/// <summary>A provider webhook translated into our own vocabulary.</summary>
public sealed record ProviderWebhookEvent(
    string EventId,
    ProviderWebhookEventType Type,
    Guid? PaymentId,
    string? ProviderReference,
    Money? Amount = null,
    string? FailureCode = null);
