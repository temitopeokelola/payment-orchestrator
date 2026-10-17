using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentOrchestrator.Application.Incidents;
using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.Infrastructure.Persistence;

public sealed class PaymentAttemptRecord
{
    public long Id { get; init; }
    public Guid PaymentId { get; init; }
    public required string Provider { get; init; }
    public required string Operation { get; init; }
    public required string Outcome { get; init; }
    public string? Code { get; init; }
    public double LatencyMs { get; init; }
    public DateTimeOffset At { get; init; }
}

internal sealed class PaymentAttemptRecordConfiguration : IEntityTypeConfiguration<PaymentAttemptRecord>
{
    public void Configure(EntityTypeBuilder<PaymentAttemptRecord> builder)
    {
        builder.ToTable("payment_attempts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.Provider).HasMaxLength(32);
        builder.Property(a => a.Operation).HasMaxLength(16);
        builder.Property(a => a.Outcome).HasMaxLength(16);
        builder.Property(a => a.Code).HasMaxLength(64);
        builder.HasIndex(a => new { a.PaymentId, a.At });
        builder.HasIndex(a => new { a.Provider, a.At });
    }
}

internal sealed class EfPaymentAttemptLog(PaymentsDbContext db) : IPaymentAttemptLog, IAttemptStatistics
{
    public void Record(PaymentAttempt attempt) => db.Set<PaymentAttemptRecord>().Add(new PaymentAttemptRecord
    {
        PaymentId = attempt.PaymentId,
        Provider = attempt.Provider,
        Operation = attempt.Operation,
        Outcome = attempt.Outcome,
        Code = attempt.Code,
        LatencyMs = attempt.LatencyMs,
        At = attempt.At,
    });

    public async Task<IReadOnlyList<PaymentAttempt>> ListAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await db.Set<PaymentAttemptRecord>()
            .AsNoTracking()
            .Where(a => a.PaymentId == paymentId)
            .OrderBy(a => a.Id)
            .Select(a => new PaymentAttempt(a.PaymentId, a.Provider, a.Operation, a.Outcome, a.Code, a.LatencyMs, a.At))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AttemptGroup>> GroupSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.Set<PaymentAttemptRecord>()
            .AsNoTracking()
            .Where(a => a.At >= since)
            .GroupBy(a => new { a.Provider, a.Operation, a.Outcome, a.Code })
            .Select(g => new AttemptGroup(g.Key.Provider, g.Key.Operation, g.Key.Outcome, g.Key.Code, g.Count(), Math.Round(g.Average(a => a.LatencyMs), 1)))
            .OrderByDescending(g => g.Count)
            .ToListAsync(cancellationToken);
}
