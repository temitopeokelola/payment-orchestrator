using System.Text;
using PaymentOrchestrator.Application.Outbox;
using RabbitMQ.Client;

namespace PaymentOrchestrator.Infrastructure.Messaging;

/// <summary>
/// Publishes with publisher confirms: <see cref="PublishAsync"/> only returns once the broker has accepted the
/// message, so the outbox never marks a message published that RabbitMQ did not take.
/// </summary>
internal sealed class RabbitMqPublisher(RabbitMqConnection connection) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            var properties = new BasicProperties
            {
                MessageId = integrationEvent.Id.ToString(),
                Type = integrationEvent.Type,
                ContentType = "application/json",
                Persistent = true,
                Timestamp = new AmqpTimestamp(integrationEvent.OccurredAt.ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?> { ["merchant_id"] = integrationEvent.MerchantId },
            };

            await channel.BasicPublishAsync(
                connection.Options.Exchange,
                integrationEvent.Type,
                mandatory: false,
                properties,
                Encoding.UTF8.GetBytes(integrationEvent.Data),
                cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        var conn = await connection.GetAsync(cancellationToken);
        _channel = await conn.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await connection.DeclareTopologyAsync(_channel, cancellationToken);
        return _channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _lock.Dispose();
    }
}
