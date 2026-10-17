using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Providers;

/// <summary>
/// Adapter for an external payment provider (Stripe, Paystack, the built-in simulators).
/// Implementations translate provider responses into <see cref="ProviderResult"/> and never throw for
/// expected failures such as declines or timeouts.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    ProviderCapabilities Capabilities { get; }

    Task<ProviderResult> AuthorizeAsync(AuthorizeCommand command, CancellationToken cancellationToken);

    Task<ProviderResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken);

    Task<ProviderResult> RefundAsync(Guid paymentId, string providerReference, Money amount, string refundKey, CancellationToken cancellationToken);

    Task<ProviderResult> CancelAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken);

    /// <summary>Verifies the webhook signature and translates the payload. Throws <see cref="WebhookSignatureException"/> if invalid.</summary>
    ProviderWebhookEvent ParseWebhook(IReadOnlyDictionary<string, string> headers, string body);
}

public sealed record ProviderCapabilities(
    IReadOnlySet<string> Currencies,
    bool SupportsSeparateCapture,
    bool SupportsCancel,
    int FeeBasisPoints);

public sealed record AuthorizeCommand(
    Guid PaymentId,
    Money Amount,
    string PaymentMethod,
    string? CustomerEmail,
    string Description,
    bool Capture);

public enum ProviderOutcome
{
    /// <summary>The provider accepted the operation.</summary>
    Approved,

    /// <summary>The provider rejected the operation (card declined, insufficient funds).</summary>
    Declined,

    /// <summary>The request never reached the provider or was refused before processing. Safe to try another provider.</summary>
    Unavailable,

    /// <summary>The request may or may not have been processed (timeout). Never retry on another provider.</summary>
    Unknown,
}

public sealed record ProviderResult(
    ProviderOutcome Outcome,
    string? ProviderReference = null,
    bool Captured = false,
    string? Code = null,
    string? Message = null)
{
    public static ProviderResult Approved(string reference, bool captured = false) => new(ProviderOutcome.Approved, reference, captured);

    public static ProviderResult Declined(string code, string? message = null) => new(ProviderOutcome.Declined, Code: code, Message: message);

    public static ProviderResult Unavailable(string code, string? message = null) => new(ProviderOutcome.Unavailable, Code: code, Message: message);

    public static ProviderResult Unknown(string code, string? message = null) => new(ProviderOutcome.Unknown, Code: code, Message: message);
}

public sealed class WebhookSignatureException(string message) : Exception(message);
