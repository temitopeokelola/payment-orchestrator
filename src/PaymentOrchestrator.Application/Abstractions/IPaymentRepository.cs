using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Abstractions;

public interface IPaymentRepository
{
    void Add(Payment payment);

    Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken cancellationToken);

    /// <summary>Looks up a payment without a merchant scope. Only for trusted internal callers (webhooks, jobs).</summary>
    Task<Payment?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Payments routed to a provider and created in the period, tracked for update. Used by reconciliation.</summary>
    Task<IReadOnlyList<Payment>> ListForProviderAsync(string provider, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
}
