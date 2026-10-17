using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Common;

namespace PaymentOrchestrator.Infrastructure.Persistence;

public interface IDomainEventDispatcher
{
    Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>Resolves subscribers lazily, so subscribers can depend on the DbContext without a constructor cycle.</summary>
internal sealed class DomainEventDispatcher(IServiceProvider services) : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, (Type SubscriberType, MethodInfo Handle)> Cache = new();

    public async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var (subscriberType, handle) = Cache.GetOrAdd(domainEvent.GetType(), eventType =>
        {
            var type = typeof(IDomainEventSubscriber<>).MakeGenericType(eventType);
            return (type, type.GetMethod(nameof(IDomainEventSubscriber<IDomainEvent>.HandleAsync))!);
        });

        foreach (var subscriber in services.GetServices(subscriberType))
        {
            await (Task)handle.Invoke(subscriber, [domainEvent, cancellationToken])!;
        }
    }
}
