using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Outbox;
using PaymentOrchestrator.Application.Webhooks;

namespace PaymentOrchestrator.Infrastructure.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Used when no broker is configured (local runs without RabbitMQ): hands events straight to the consumers
/// in-process. Same contract, no durability across restarts beyond the outbox itself.
/// </summary>
internal sealed class InMemoryEventPublisher(IServiceScopeFactory scopes) : IEventPublisher
{
    public async Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MerchantWebhookFanout>().HandleAsync(integrationEvent, cancellationToken);
    }
}
