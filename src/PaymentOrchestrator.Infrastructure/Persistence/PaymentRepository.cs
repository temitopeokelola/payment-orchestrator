using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Infrastructure.Persistence;

internal sealed class PaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public void Add(Payment payment) => db.Payments.Add(payment);

    public Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken cancellationToken) =>
        db.Payments.SingleOrDefaultAsync(p => p.Id == id && p.MerchantId == merchantId, cancellationToken);

    // FindAsync checks entities already tracked in this unit of work before querying, which event subscribers rely on.
    public async Task<Payment?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Payments.FindAsync([id], cancellationToken);

    public async Task<IReadOnlyList<Payment>> ListForProviderAsync(string provider, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
        await db.Payments
            .Where(p => p.Provider == provider && p.CreatedAt >= since && p.CreatedAt < until)
            .ToListAsync(cancellationToken);
}
