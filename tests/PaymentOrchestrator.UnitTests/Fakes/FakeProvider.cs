using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.UnitTests.Fakes;

internal sealed class FakeProvider(
    string name,
    int feeBasisPoints = 100,
    bool separateCapture = true,
    params string[] currencies) : IPaymentProvider
{
    public string Name => name;

    public ProviderCapabilities Capabilities { get; } = new(
        (currencies.Length == 0 ? ["NGN", "USD"] : currencies).ToHashSet(),
        separateCapture,
        SupportsCancel: true,
        feeBasisPoints);

    public Queue<ProviderResult> AuthorizeResults { get; } = new();

    public int AuthorizeCalls { get; private set; }

    public Task<ProviderResult> AuthorizeAsync(AuthorizeCommand command, CancellationToken cancellationToken)
    {
        AuthorizeCalls++;
        return Task.FromResult(AuthorizeResults.Count > 0
            ? AuthorizeResults.Dequeue()
            : ProviderResult.Approved($"{name}_ref", captured: command.Capture));
    }

    public Task<ProviderResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult.Approved(providerReference, captured: true));

    public Task<ProviderResult> RefundAsync(Guid paymentId, string providerReference, Money amount, string refundKey, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult.Approved(refundKey));

    public Task<ProviderResult> CancelAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult.Approved(providerReference));

    public ProviderWebhookEvent ParseWebhook(IReadOnlyDictionary<string, string> headers, string body) =>
        throw new NotSupportedException();
}
