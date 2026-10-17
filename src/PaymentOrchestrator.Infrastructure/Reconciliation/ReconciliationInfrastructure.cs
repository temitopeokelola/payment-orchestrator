using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentOrchestrator.Application.Reconciliation;
using PaymentOrchestrator.Infrastructure.Persistence;
using PaymentOrchestrator.Infrastructure.Providers.Simulator;

namespace PaymentOrchestrator.Infrastructure.Reconciliation;

/// <summary>The simulator's books, presented as a settlement report.</summary>
internal sealed class SimulatorSettlementSource(string provider, SimulatorLedger ledger) : ISettlementReportSource
{
    public string Provider => provider;

    public Task<IReadOnlyList<SettlementRecord>> GetAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SettlementRecord>>([
            .. ledger.Transactions(provider)
                .Where(t => t.CreatedAt >= since && t.CreatedAt < until && t.Status != SimulatedStatus.Cancelled)
                .Select(t => new SettlementRecord(
                    t.Reference,
                    t.PaymentId,
                    t.Amount,
                    t.Status == SimulatedStatus.Captured ? t.Amount.AmountMinor : 0,
                    t.RefundedMinor,
                    t.CreatedAt)),
        ]);
}

public sealed class ReconciliationRunRecord
{
    public Guid Id { get; init; }
    public required string Provider { get; init; }
    public DateTimeOffset PeriodFrom { get; init; }
    public DateTimeOffset PeriodTo { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public int Matched { get; init; }
    public int Unresolved { get; init; }
    public required string Items { get; init; }
}

internal sealed class ReconciliationRunRecordConfiguration : IEntityTypeConfiguration<ReconciliationRunRecord>
{
    public void Configure(EntityTypeBuilder<ReconciliationRunRecord> builder)
    {
        builder.ToTable("reconciliation_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Provider).HasMaxLength(32);
        builder.Property(r => r.Items).HasColumnType("jsonb");
        builder.HasIndex(r => new { r.Provider, r.StartedAt });
    }
}

internal sealed class EfReconciliationStore(PaymentsDbContext db) : IReconciliationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(ReconciliationRun run) => db.Set<ReconciliationRunRecord>().Add(new ReconciliationRunRecord
    {
        Id = run.Id,
        Provider = run.Provider,
        PeriodFrom = run.From,
        PeriodTo = run.To,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        Matched = run.Matched,
        Unresolved = run.Unresolved,
        Items = JsonSerializer.Serialize(run.Items, Json),
    });

    public async Task<ReconciliationRun?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<ReconciliationRunRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is { } record
            ? ToRun(record)
            : null;

    public async Task<IReadOnlyList<ReconciliationRun>> ListAsync(string? provider, int limit, CancellationToken cancellationToken)
    {
        var query = db.Set<ReconciliationRunRecord>().AsNoTracking();
        if (provider is not null)
        {
            query = query.Where(r => r.Provider == provider);
        }

        var records = await query.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(cancellationToken);
        return [.. records.Select(ToRun)];
    }

    private static ReconciliationRun ToRun(ReconciliationRunRecord r) => new(
        r.Id, r.Provider, r.PeriodFrom, r.PeriodTo, r.StartedAt, r.CompletedAt, r.Matched,
        JsonSerializer.Deserialize<List<ReconciliationItem>>(r.Items, Json) ?? []);
}

/// <summary>Reconciles every provider on a schedule, skipping the most recent minutes where webhooks may be in flight.</summary>
internal sealed partial class ReconciliationWorker(
    IServiceScopeFactory scopes,
    IOptions<ReconciliationOptions> options,
    TimeProvider clock,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (settings.IntervalMinutes <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.IntervalMinutes), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var until = clock.GetUtcNow().AddMinutes(-settings.SettleDelayMinutes);
            var since = until.AddHours(-settings.LookbackHours);

            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();

            foreach (var provider in service.Providers)
            {
                try
                {
                    var run = await service.RunAsync(provider, since, until, stoppingToken);
                    if (run is { Unresolved: > 0 })
                    {
                        LogUnresolved(provider, run.Id, run.Unresolved);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(ex, provider);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation run {RunId} for {Provider} has {Count} unresolved differences")]
    private partial void LogUnresolved(string provider, Guid runId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation for {Provider} failed")]
    private partial void LogRunFailed(Exception exception, string provider);
}
