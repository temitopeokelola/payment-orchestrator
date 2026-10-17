using RabbitMQ.Client;

namespace PaymentOrchestrator.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672</c>. When empty, events are dispatched in-process.</summary>
    public string? ConnectionString { get; set; }

    public string Exchange { get; set; } = "payments.events";
    public string DeadLetterExchange { get; set; } = "payments.events.dlx";
    public string WebhookQueue { get; set; } = "merchant-webhooks";
}

/// <summary>One long-lived connection per process, as RabbitMQ recommends; channels are created per use.</summary>
internal sealed class RabbitMqConnection(RabbitMqOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqOptions Options => options;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.ConnectionString!),
                AutomaticRecoveryEnabled = true,
                ClientProvidedName = "payment-orchestrator",
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Declares the topology. Idempotent, so every component can call it on startup.</summary>
    public async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync($"{options.WebhookQueue}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync($"{options.WebhookQueue}.dlq", options.DeadLetterExchange, string.Empty, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            options.WebhookQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = options.DeadLetterExchange },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(options.WebhookQueue, options.Exchange, "payment.#", cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _lock.Dispose();
    }
}
