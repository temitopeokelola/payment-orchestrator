using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Persistence;

namespace PaymentOrchestrator.Infrastructure.Webhooks;

internal sealed class PostgresWebhookInbox(PaymentsDbContext db, TimeProvider clock) : IProviderWebhookInbox
{
    public async Task<bool> TryRecordAsync(string provider, ProviderWebhookEvent webhookEvent, string payload, CancellationToken cancellationToken)
    {
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO provider_webhook_events (provider, event_id, type, payment_id, payload, received_at)
            VALUES ({provider}, {webhookEvent.EventId}, {webhookEvent.Type.ToString()}, {webhookEvent.PaymentId}, CAST({payload} AS jsonb), {clock.GetUtcNow()})
            ON CONFLICT (provider, event_id) DO NOTHING
            """,
            cancellationToken);

        return inserted == 1;
    }
}
