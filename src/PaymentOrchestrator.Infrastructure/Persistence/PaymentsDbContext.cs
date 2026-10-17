using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.Infrastructure.Idempotency;
using PaymentOrchestrator.Infrastructure.Webhooks;

namespace PaymentOrchestrator.Infrastructure.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<ProviderWebhookEventRecord> ProviderWebhookEvents => Set<ProviderWebhookEventRecord>();

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) =>
        await SaveChangesAsync(cancellationToken);

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);
}
