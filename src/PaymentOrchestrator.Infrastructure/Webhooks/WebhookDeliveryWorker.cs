using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Outbox;
using PaymentOrchestrator.Infrastructure.Persistence;

namespace PaymentOrchestrator.Infrastructure.Webhooks;

/// <summary>
/// Sends due webhook deliveries. Failures are retried with exponential backoff and jitter; after the maximum
/// number of attempts a delivery is marked dead (the application-level dead-letter queue) and can be replayed.
/// </summary>
internal sealed partial class WebhookDeliveryWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IMerchantWebhookDirectory directory,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    public const string HttpClientName = WebhookSigner.HttpClientName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Value.WebhookPollMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            int sent;
            try
            {
                sent = await DeliverDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBatchFailed(ex);
                sent = 0;
            }

            if (sent < options.Value.WebhookBatchSize)
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
        }
    }

    internal async Task<int> DeliverDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var due = await db.Set<WebhookDelivery>()
            .FromSqlInterpolated($"""
                SELECT * FROM webhook_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at
                LIMIT {options.Value.WebhookBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        await Task.WhenAll(due.Select(d => AttemptAsync(d, cancellationToken)));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }

    private async Task AttemptAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        delivery.Attempts++;

        var endpoint = directory.Find(delivery.MerchantId);
        if (endpoint is null)
        {
            delivery.Status = WebhookDeliveryStatus.Dead;
            delivery.LastError = "Merchant has no webhook endpoint configured.";
            delivery.NextAttemptAt = null;
            return;
        }

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
        {
            Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSigner.IdHeader, delivery.EventId.ToString());
        request.Headers.Add(WebhookSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSigner.SignatureHeader, WebhookSigner.Sign(endpoint.Secret, timestamp, delivery.Payload));

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            delivery.LastStatusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = WebhookDeliveryStatus.Delivered;
                delivery.DeliveredAt = clock.GetUtcNow();
                delivery.NextAttemptAt = null;
                delivery.LastError = null;
                return;
            }

            ScheduleRetry(delivery, $"Endpoint returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            delivery.LastStatusCode = null;
            ScheduleRetry(delivery, ex.Message);
        }
    }

    private void ScheduleRetry(WebhookDelivery delivery, string error)
    {
        delivery.LastError = error.Length > 1024 ? error[..1024] : error;

        if (delivery.Attempts >= options.Value.WebhookMaxAttempts)
        {
            delivery.Status = WebhookDeliveryStatus.Dead;
            delivery.NextAttemptAt = null;
            LogDeadLettered(delivery.Id, delivery.EventType, delivery.Attempts);
            return;
        }

        delivery.NextAttemptAt = clock.GetUtcNow() + Backoff(delivery.Attempts, options.Value.WebhookRetryBaseSeconds);
    }

    /// <summary>base, 2x, 4x ... (10s, 20s, 40s by default) capped at one hour, with up to 20% jitter so retries don't arrive in lockstep.</summary>
    internal static TimeSpan Backoff(int attempt, double baseSeconds)
    {
        var seconds = Math.Min(3_600, baseSeconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds * (1 + (Random.Shared.NextDouble() * 0.2)));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook delivery batch failed")]
    private partial void LogBatchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} ({EventType}) dead after {Attempts} attempts")]
    private partial void LogDeadLettered(Guid deliveryId, string eventType, int attempts);
}
