using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.UnitTests.Fakes;

internal sealed class InMemoryAttemptLog : IPaymentAttemptLog
{
    public List<PaymentAttempt> Attempts { get; } = [];

    public void Record(PaymentAttempt attempt) => Attempts.Add(attempt);

    public Task<IReadOnlyList<PaymentAttempt>> ListAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentAttempt>>([.. Attempts.Where(a => a.PaymentId == paymentId)]);
}
