namespace PaymentOrchestrator.Infrastructure.Idempotency;

public sealed class IdempotencyRecord
{
    public required string MerchantId { get; init; }
    public required string Key { get; init; }
    public required string RequestHash { get; init; }
    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
}
