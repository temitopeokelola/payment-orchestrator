using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Abstractions;

public interface IPaymentRepository
{
    void Add(Payment payment);

    Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken cancellationToken);
}
