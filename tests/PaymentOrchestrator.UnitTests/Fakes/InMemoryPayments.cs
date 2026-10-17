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

    public Task<Payment?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_payments.GetValueOrDefault(id));

    public Task<IReadOnlyList<Payment>> ListForProviderAsync(string provider, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Payment>>([.. _payments.Values.Where(p => p.Provider == provider && p.CreatedAt >= since && p.CreatedAt < until)]);

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        work(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
