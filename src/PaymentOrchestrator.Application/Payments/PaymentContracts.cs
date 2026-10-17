using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Payments;

/// <param name="Amount">Amount in the currency's minor unit.</param>
/// <param name="PaymentMethod">Provider token, for example <c>pm_card_visa</c> (Stripe) or <c>sim_card_approved</c> (simulator).</param>
/// <param name="Capture">Capture immediately (default) or only authorize and capture later.</param>
/// <param name="Provider">Optional preferred provider. Routing still falls back to others if it is unavailable.</param>
public sealed record CreatePaymentRequest(
    long Amount,
    string Currency,
    string? Description,
    string PaymentMethod,
    string? CustomerEmail = null,
    bool Capture = true,
    string? Provider = null);

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
