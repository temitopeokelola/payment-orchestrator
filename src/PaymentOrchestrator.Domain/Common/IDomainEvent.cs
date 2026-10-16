namespace PaymentOrchestrator.Domain.Common;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
