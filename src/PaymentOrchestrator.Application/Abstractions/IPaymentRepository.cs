using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Abstractions;

public interface IPaymentRepository
{
    void Add(Payment payment);

    Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken cancellationToken);

    /// <summary>Looks up a payment without a merchant scope. Only for trusted internal callers (webhooks, jobs).</summary>
    Task<Payment?> FindAsync(Guid id, CancellationToken cancellationToken);
}
