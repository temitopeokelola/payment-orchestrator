using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Common;
using PaymentOrchestrator.Domain.Ledger;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.Infrastructure.Idempotency;
using PaymentOrchestrator.Infrastructure.Webhooks;

namespace PaymentOrchestrator.Infrastructure.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, IDomainEventDispatcher? dispatcher = null)
    : DbContext(options), IUnitOfWork
{
    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<ProviderWebhookEventRecord> ProviderWebhookEvents => Set<ProviderWebhookEventRecord>();

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) =>
        await SaveChangesAsync(cancellationToken);

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await DispatchDomainEventsAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);

    /// <summary>
    /// Runs subscribers before saving so whatever they add (ledger entries, outbox messages) commits in the same
    /// transaction as the change that raised the event. Loops because a subscriber may raise further events.
    /// </summary>
    private async Task DispatchDomainEventsAsync(CancellationToken cancellationToken)
    {
        if (dispatcher is null)
        {
            return;
        }

        while (true)
        {
            var events = ChangeTracker.Entries<AggregateRoot>()
                .SelectMany(entry =>
                {
                    var raised = entry.Entity.DomainEvents.ToArray();
                    entry.Entity.ClearDomainEvents();
                    return raised;
                })
                .ToList();

            if (events.Count == 0)
            {
                return;
            }

            foreach (var domainEvent in events)
            {
                await dispatcher.DispatchAsync(domainEvent, cancellationToken);
            }
        }
    }
}
