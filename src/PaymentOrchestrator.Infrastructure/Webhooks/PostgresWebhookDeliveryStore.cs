using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Outbox;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Persistence;

namespace PaymentOrchestrator.Infrastructure.Webhooks;

internal sealed class PostgresWebhookDeliveryStore(PaymentsDbContext db, TimeProvider clock) : IWebhookDeliveryStore
{
    public async Task ScheduleAsync(IntegrationEvent integrationEvent, MerchantWebhookEndpoint endpoint, CancellationToken cancellationToken)
    {
        // The body merchants receive: an envelope around the resource snapshot.
        var payload = new JsonObject
        {
            ["id"] = integrationEvent.Id,
            ["type"] = integrationEvent.Type,
            ["created_at"] = integrationEvent.OccurredAt,
            ["data"] = JsonNode.Parse(integrationEvent.Data),
        }.ToJsonString();

        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO webhook_deliveries (id, event_id, event_type, merchant_id, url, payload, status, attempts, created_at, next_attempt_at)
            VALUES ({Guid.CreateVersion7(now)}, {integrationEvent.Id}, {integrationEvent.Type}, {endpoint.MerchantId}, {endpoint.Url.ToString()},
                    CAST({payload} AS jsonb), 'Pending', 0, {now}, {now})
            ON CONFLICT (event_id, url) DO NOTHING
            """,
            cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookDeliveryResponse>> ListAsync(string merchantId, WebhookDeliveryStatus? status, int limit, CancellationToken cancellationToken)
    {
        var query = db.Set<WebhookDelivery>().AsNoTracking().Where(d => d.MerchantId == merchantId);
        if (status is { } s)
        {
            query = query.Where(d => d.Status == s);
        }

        var rows = await query.OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(cancellationToken);
        return [.. rows.Select(d => d.ToResponse())];
    }

    public async Task<WebhookDeliveryResponse?> ReplayAsync(string merchantId, Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await db.Set<WebhookDelivery>().SingleOrDefaultAsync(d => d.Id == deliveryId && d.MerchantId == merchantId, cancellationToken);
        if (delivery is null)
        {
            return null;
        }

        delivery.Status = WebhookDeliveryStatus.Pending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = clock.GetUtcNow();
        delivery.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
        return delivery.ToResponse();
    }
}

