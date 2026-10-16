using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Idempotency;
using PaymentOrchestrator.Infrastructure.Persistence;

namespace PaymentOrchestrator.Infrastructure.Idempotency;

internal sealed class PostgresIdempotencyStore(PaymentsDbContext db, TimeProvider clock) : IIdempotencyStore
{
    public async Task<IdempotencyClaim> TryClaimAsync(string merchantId, string key, string requestHash, CancellationToken cancellationToken)
    {
        // The primary key on (merchant_id, key) makes this insert the lock: concurrent duplicates insert zero rows.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO idempotency_keys (merchant_id, key, request_hash, created_at)
            VALUES ({merchantId}, {key}, {requestHash}, {clock.GetUtcNow()})
            ON CONFLICT (merchant_id, key) DO NOTHING
            """,
            cancellationToken);

        if (inserted == 1)
        {
            return new IdempotencyClaim.Claimed();
        }

        var existing = await db.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.MerchantId == merchantId && r.Key == key, cancellationToken);

        return existing switch
        {
            // Released between our insert and read; treat as in progress and let the client retry.
            null => new IdempotencyClaim.InProgress(),
            _ when existing.RequestHash != requestHash => new IdempotencyClaim.PayloadMismatch(),
            { StatusCode: { } status, ResponseBody: { } body } => new IdempotencyClaim.Completed(status, body),
            _ => new IdempotencyClaim.InProgress(),
        };
    }

    public async Task CompleteAsync(string merchantId, string key, int statusCode, string responseBody, CancellationToken cancellationToken) =>
        await db.IdempotencyRecords
            .Where(r => r.MerchantId == merchantId && r.Key == key)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.StatusCode, statusCode)
                    .SetProperty(r => r.ResponseBody, responseBody)
                    .SetProperty(r => r.CompletedAt, clock.GetUtcNow()),
                cancellationToken);

    public async Task ReleaseAsync(string merchantId, string key, CancellationToken cancellationToken) =>
        await db.IdempotencyRecords
            .Where(r => r.MerchantId == merchantId && r.Key == key && r.CompletedAt == null)
            .ExecuteDeleteAsync(cancellationToken);
}
