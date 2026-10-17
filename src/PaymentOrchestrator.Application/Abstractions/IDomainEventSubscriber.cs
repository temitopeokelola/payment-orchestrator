using PaymentOrchestrator.Domain.Common;

namespace PaymentOrchestrator.Application.Abstractions;

/// <summary>
/// Handles a domain event inside the same unit of work that raised it. Anything the handler adds is saved
/// atomically with the change that caused it.
/// </summary>
public interface IDomainEventSubscriber<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
