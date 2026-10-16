using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Infrastructure.Persistence;

internal sealed class PaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public void Add(Payment payment) => db.Payments.Add(payment);

    public Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken cancellationToken) =>
        db.Payments.SingleOrDefaultAsync(p => p.Id == id && p.MerchantId == merchantId, cancellationToken);
}
