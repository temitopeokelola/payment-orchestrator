using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentOrchestrator.Application.Outbox;
using PaymentOrchestrator.Application.Webhooks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PaymentOrchestrator.Infrastructure.Messaging;

/// <summary>
/// Consumes payment events and schedules merchant webhook deliveries. Messages are acked only after the
/// delivery row is committed; malformed messages are rejected to the dead-letter queue instead of looping.
/// </summary>
internal sealed partial class RabbitMqWebhookConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopes,
    ILogger<RabbitMqWebhookConsumer> logger) : BackgroundService
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var conn = await connection.GetAsync(stoppingToken);
                _channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
                await connection.DeclareTopologyAsync(_channel, stoppingToken);
                await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 20, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += (_, delivery) => HandleAsync(_channel, delivery, stoppingToken);
                await _channel.BasicConsumeAsync(connection.Options.WebhookQueue, autoAck: false, consumer, stoppingToken);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogConsumerFailed(ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        IntegrationEvent integrationEvent;
        try
        {
            var merchantId = delivery.BasicProperties.Headers?.TryGetValue("merchant_id", out var raw) == true && raw is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : throw new FormatException("Missing merchant_id header.");

            integrationEvent = new IntegrationEvent(
                Guid.Parse(delivery.BasicProperties.MessageId!),
                delivery.BasicProperties.Type ?? delivery.RoutingKey,
                merchantId,
                DateTimeOffset.FromUnixTimeSeconds(delivery.BasicProperties.Timestamp.UnixTime),
                Encoding.UTF8.GetString(delivery.Body.Span));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            LogPoisonMessage(ex, delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MerchantWebhookFanout>().HandleAsync(integrationEvent, cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient failure (database down): requeue and try again.
            LogHandlingFailed(ex, integrationEvent.Id);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "RabbitMQ consumer failed; reconnecting")]
    private partial void LogConsumerFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rejecting malformed message {MessageId} to the dead-letter queue")]
    private partial void LogPoisonMessage(Exception exception, string? messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to handle event {EventId}; requeueing")]
    private partial void LogHandlingFailed(Exception exception, Guid eventId);
}
