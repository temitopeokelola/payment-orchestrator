using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Payments;

/// <summary>Stands in for payments that never reached a provider. Any provider call is a bug.</summary>
internal sealed class NullProvider : IPaymentProvider
{
    public static readonly NullProvider Instance = new();

    public string Name => "none";

    public ProviderCapabilities Capabilities { get; } = new(new HashSet<string>(), false, false, 0);

    public Task<ProviderResult> AuthorizeAsync(AuthorizeCommand command, CancellationToken cancellationToken) => throw NotRouted();

    public Task<ProviderResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) => throw NotRouted();

    public Task<ProviderResult> RefundAsync(Guid paymentId, string providerReference, Money amount, string refundKey, CancellationToken cancellationToken) => throw NotRouted();

    public Task<ProviderResult> CancelAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken) => throw NotRouted();

    public ProviderWebhookEvent ParseWebhook(IReadOnlyDictionary<string, string> headers, string body) => throw NotRouted();

    private static InvalidOperationException NotRouted() => new("Payment was never routed to a provider.");
}
