namespace PaymentOrchestrator.Domain.Payments;

public enum PaymentStatus
{
    Created = 0,
    Authorized = 1,
    Captured = 2,
    PartiallyRefunded = 3,
    Refunded = 4,
    Failed = 5,
    Cancelled = 6,
}
