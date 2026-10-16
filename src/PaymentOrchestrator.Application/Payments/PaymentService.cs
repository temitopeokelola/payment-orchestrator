using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Payments;

public sealed class PaymentService(IPaymentRepository payments, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<PaymentResponse> CreateAsync(string merchantId, CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = Payment.Create(
            merchantId,
            new Money(request.Amount, request.Currency),
            request.Description ?? string.Empty,
            clock.GetUtcNow());

        payments.Add(payment);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PaymentResponse.From(payment);
    }

    public async Task<PaymentResponse?> GetAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(paymentId, merchantId, cancellationToken);
        return payment is null ? null : PaymentResponse.From(payment);
    }

    public Task<PaymentResponse?> CaptureAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken) =>
        MutateAsync(merchantId, paymentId, p => p.Capture(clock.GetUtcNow()), cancellationToken);

    public Task<PaymentResponse?> CancelAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken) =>
        MutateAsync(merchantId, paymentId, p => p.Cancel(clock.GetUtcNow()), cancellationToken);

    public Task<PaymentResponse?> RefundAsync(string merchantId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken) =>
        MutateAsync(
            merchantId,
            paymentId,
            p => p.Refund(new Money(request.Amount, p.Amount.Currency), clock.GetUtcNow()),
            cancellationToken);

    private async Task<PaymentResponse?> MutateAsync(
        string merchantId,
        Guid paymentId,
        Action<Payment> change,
        CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(paymentId, merchantId, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        change(payment);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PaymentResponse.From(payment);
    }
}
