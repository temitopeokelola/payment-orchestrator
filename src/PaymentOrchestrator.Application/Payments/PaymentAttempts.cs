namespace PaymentOrchestrator.Application.Payments;

/// <summary>One call to a provider on behalf of a payment, kept for support, auditing and routing analysis.</summary>
public sealed record PaymentAttempt(
    Guid PaymentId,
    string Provider,
    string Operation,
    string Outcome,
    string? Code,
    double LatencyMs,
    DateTimeOffset At);

public interface IPaymentAttemptLog
{
    /// <summary>Adds the attempt to the current unit of work; it is saved with the payment.</summary>
    void Record(PaymentAttempt attempt);

    Task<IReadOnlyList<PaymentAttempt>> ListAsync(Guid paymentId, CancellationToken cancellationToken);
}
