namespace PaymentOrchestrator.Application.Idempotency;

public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims the key for this request. Only one concurrent caller can win the claim.
    /// </summary>
    Task<IdempotencyClaim> TryClaimAsync(string merchantId, string key, string requestHash, CancellationToken cancellationToken);

    Task CompleteAsync(string merchantId, string key, int statusCode, string responseBody, CancellationToken cancellationToken);

    /// <summary>Removes an unfinished claim so the client can safely retry after a failure.</summary>
    Task ReleaseAsync(string merchantId, string key, CancellationToken cancellationToken);
}

public abstract record IdempotencyClaim
{
    /// <summary>The key is new; the caller owns it and must complete or release it.</summary>
    public sealed record Claimed : IdempotencyClaim;

    /// <summary>The key was already processed; return the stored response.</summary>
    public sealed record Completed(int StatusCode, string ResponseBody) : IdempotencyClaim;

    /// <summary>Another request with this key is still being processed.</summary>
    public sealed record InProgress : IdempotencyClaim;

    /// <summary>The key was used before with a different request payload.</summary>
    public sealed record PayloadMismatch : IdempotencyClaim;
}
