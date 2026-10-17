using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentOrchestrator.Infrastructure.Messaging;
using PaymentOrchestrator.Infrastructure.Persistence;

namespace PaymentOrchestrator.Infrastructure.Outbox;

public sealed class WorkerOptions
{
    public const string SectionName = "Workers";

    public int OutboxPollMilliseconds { get; set; } = 1_000;
    public int OutboxBatchSize { get; set; } = 50;
    public int WebhookPollMilliseconds { get; set; } = 1_000;
    public int WebhookBatchSize { get; set; } = 20;
    public int WebhookMaxAttempts { get; set; } = 8;
    public double WebhookRetryBaseSeconds { get; set; } = 10;
}

/// <summary>
/// Publishes outbox messages in order. Rows are locked with <c>FOR UPDATE SKIP LOCKED</c>, so several API
/// instances can run the processor concurrently without publishing the same message twice at the same time.
/// Delivery is at least once: a crash after publishing but before marking the row republishes it, and consumers
/// deduplicate by message id.
/// </summary>
internal sealed partial class OutboxProcessor(
    IServiceScopeFactory scopes,
    IEventPublisher publisher,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Value.OutboxPollMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                published = await PublishBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBatchFailed(ex);
                published = 0;
            }

            // Keep draining while there is a backlog; otherwise wait for the next poll.
            if (published < options.Value.OutboxBatchSize)
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
        }
    }

    internal async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await db.Set<OutboxMessage>()
            .FromSqlInterpolated($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY occurred_at, id
                LIMIT {options.Value.OutboxBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(message.ToIntegrationEvent(), cancellationToken);
                message.PublishedAt = clock.GetUtcNow();
                published++;

                var type = new KeyValuePair<string, object?>("type", message.Type);
                InfrastructureTelemetry.OutboxPublished.Add(1, type);
                InfrastructureTelemetry.OutboxLag.Record((message.PublishedAt.Value - message.OccurredAt).TotalSeconds, type);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message;
                LogPublishFailed(ex, message.Id, message.Type);

                // Stop here so later events for the same payment are not published ahead of this one.
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed")]
    private partial void LogBatchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish outbox message {MessageId} ({Type}); will retry")]
    private partial void LogPublishFailed(Exception exception, Guid messageId, string type);
}
