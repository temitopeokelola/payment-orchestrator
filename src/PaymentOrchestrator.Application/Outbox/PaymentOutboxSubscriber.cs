using System.Text.Json;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Domain.Common;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Outbox;

/// <summary>Turns payment domain events into outbox messages carrying a snapshot of the payment.</summary>
public sealed class PaymentOutboxSubscriber(IOutbox outbox, IPaymentRepository payments) :
    IDomainEventSubscriber<PaymentCreated>,
    IDomainEventSubscriber<PaymentAuthorized>,
    IDomainEventSubscriber<PaymentCaptured>,
    IDomainEventSubscriber<PaymentRefunded>,
    IDomainEventSubscriber<PaymentFailed>,
    IDomainEventSubscriber<PaymentCancelled>
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public Task HandleAsync(PaymentCreated domainEvent, CancellationToken cancellationToken) =>
        EnqueueAsync(IntegrationEventTypes.PaymentCreated, domainEvent.PaymentId, domainEvent, cancellationToken);

    public Task HandleAsync(PaymentAuthorized domainEvent, CancellationToken cancellationToken) =>
        EnqueueAsync(IntegrationEventTypes.PaymentAuthorized, domainEvent.PaymentId, domainEvent, cancellationToken);

    public Task HandleAsync(PaymentCaptured domainEvent, CancellationToken cancellationToken) =>
        EnqueueAsync(IntegrationEventTypes.PaymentCaptured, domainEvent.PaymentId, domainEvent, cancellationToken);

    public Task HandleAsync(PaymentRefunded domainEvent, CancellationToken cancellationToken) =>
        EnqueueAsync(IntegrationEventTypes.PaymentRefunded, domainEvent.PaymentId, domainEvent, cancellationToken);

    public Task HandleAsync(PaymentFailed domainEvent, CancellationToken cancellationToken) =>
        EnqueueAsync(IntegrationEventTypes.PaymentFailed, domainEvent.PaymentId, domainEvent, cancellationToken);

    public Task HandleAsync(PaymentCancelled domainEvent, CancellationToken cancellationToken) =>
        EnqueueAsync(IntegrationEventTypes.PaymentCancelled, domainEvent.PaymentId, domainEvent, cancellationToken);

    private async Task EnqueueAsync(string type, Guid paymentId, IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var payment = await payments.FindAsync(paymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {paymentId} raised {type} but is not tracked.");

        var data = JsonSerializer.Serialize(PaymentResponse.From(payment), JsonOptions);
        outbox.Enqueue(new IntegrationEvent(Guid.CreateVersion7(domainEvent.OccurredAt), type, payment.MerchantId, domainEvent.OccurredAt, data));
    }
}
