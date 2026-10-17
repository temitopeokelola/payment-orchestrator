using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.UnitTests.Fakes;

internal sealed class InMemoryPayments : IPaymentRepository, IUnitOfWork
{
    private readonly Dictionary<Guid, Payment> _payments = [];

    public int SaveCount { get; private set; }

    public void Add(Payment payment) => _payments[payment.Id] = payment;

    public Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken cancellationToken) =>
        Task.FromResult(_payments.TryGetValue(id, out var p) && p.MerchantId == merchantId ? p : null);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
