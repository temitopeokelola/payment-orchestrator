namespace PaymentOrchestrator.Application.Payments;

/// <summary>A provider rejected or could not complete a follow-up operation (capture, refund, cancel).</summary>
public sealed class ProviderOperationException(string code, string message, bool retryable) : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}
