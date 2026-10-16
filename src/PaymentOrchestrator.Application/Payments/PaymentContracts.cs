using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Payments;

public sealed record CreatePaymentRequest(long Amount, string Currency, string? Description);

public sealed record RefundPaymentRequest(long Amount);

public sealed record PaymentResponse(
    Guid Id,
    string Status,
    long Amount,
    string Currency,
    string Description,
    string? Provider,
    string? ProviderReference,
    long CapturedAmount,
    long RefundedAmount,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentResponse From(Payment payment) => new(
        payment.Id,
        ToSnakeCase(payment.Status),
        payment.Amount.AmountMinor,
        payment.Amount.Currency,
        payment.Description,
        payment.Provider,
        payment.ProviderReference,
        payment.CapturedAmount.AmountMinor,
        payment.RefundedAmount.AmountMinor,
        payment.FailureReason,
        payment.CreatedAt,
        payment.UpdatedAt);

    private static string ToSnakeCase(PaymentStatus status) => status switch
    {
        PaymentStatus.PartiallyRefunded => "partially_refunded",
        _ => status.ToString().ToLowerInvariant(),
    };
}
