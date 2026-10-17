using PaymentOrchestrator.Domain.Common;

namespace PaymentOrchestrator.Domain.Payments;

public sealed class Payment : AggregateRoot
{
    // Required by EF Core.
    private Payment()
    {
        MerchantId = null!;
        Description = null!;
        PaymentMethod = null!;
    }

    private Payment(
        Guid id,
        string merchantId,
        Money amount,
        string description,
        string paymentMethod,
        string? customerEmail,
        DateTimeOffset now)
    {
        Id = id;
        MerchantId = merchantId;
        Amount = amount;
        Description = description;
        PaymentMethod = paymentMethod;
        CustomerEmail = customerEmail;
        Status = PaymentStatus.Created;
        CapturedAmount = Money.Zero(amount.Currency);
        RefundedAmount = Money.Zero(amount.Currency);
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string MerchantId { get; private set; }
    public Money Amount { get; private set; }
    public string Description { get; private set; }

    /// <summary>Provider-issued token for the card or account (never raw card data).</summary>
    public string PaymentMethod { get; private set; }

    public string? CustomerEmail { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? Provider { get; private set; }
    public string? ProviderReference { get; private set; }
    public Money CapturedAmount { get; private set; }
    public Money RefundedAmount { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Payment Create(
        string merchantId,
        Money amount,
        string description,
        string paymentMethod,
        string? customerEmail,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            throw new DomainException("payment.merchant_required", "Merchant id is required.");
        }

        if (amount.AmountMinor == 0)
        {
            throw new DomainException("payment.zero_amount", "Payment amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            throw new DomainException("payment.payment_method_required", "A payment method token is required.");
        }

        var payment = new Payment(
            Guid.CreateVersion7(now),
            merchantId,
            amount,
            description.Trim(),
            paymentMethod.Trim(),
            string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail.Trim(),
            now);

        payment.Raise(new PaymentCreated(payment.Id, merchantId, amount, now));
        return payment;
    }

    /// <summary>
    /// Records which provider is handling the payment before the outcome is known, so a timed-out
    /// attempt can still be matched to a later webhook or settlement report.
    /// </summary>
    public void AssignProvider(string provider, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Created, "assign a provider to");

        Provider = provider;
        UpdatedAt = now;
    }

    public void Authorize(string provider, string providerReference, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Created, "authorize");

        Provider = provider;
        ProviderReference = providerReference;
        Transition(PaymentStatus.Authorized, now);
        Raise(new PaymentAuthorized(Id, provider, providerReference, now));
    }

    public void Capture(DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Authorized, "capture");

        CapturedAmount = Amount;
        Transition(PaymentStatus.Captured, now);
        Raise(new PaymentCaptured(Id, MerchantId, Provider!, Amount, now));
    }

    /// <summary>Validates a refund without changing state, so it can be checked before calling the provider.</summary>
    public void EnsureCanRefund(Money amount)
    {
        if (Status is not (PaymentStatus.Captured or PaymentStatus.PartiallyRefunded))
        {
            throw InvalidTransition("refund");
        }

        if (amount.AmountMinor == 0)
        {
            throw new DomainException("payment.zero_refund", "Refund amount must be greater than zero.");
        }

        if (RefundedAmount.Add(amount).IsGreaterThan(CapturedAmount))
        {
            throw new DomainException("payment.refund_exceeds_capture", "Refund exceeds the captured amount.");
        }
    }

    public void Refund(Money amount, DateTimeOffset now)
    {
        EnsureCanRefund(amount);

        var totalRefunded = RefundedAmount.Add(amount);
        RefundedAmount = totalRefunded;
        Transition(totalRefunded == CapturedAmount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded, now);
        Raise(new PaymentRefunded(Id, MerchantId, Provider!, amount, totalRefunded, now));
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Created, "fail");

        FailureReason = reason;
        Transition(PaymentStatus.Failed, now);
        Raise(new PaymentFailed(Id, reason, now));
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Created or PaymentStatus.Authorized))
        {
            throw InvalidTransition("cancel");
        }

        Transition(PaymentStatus.Cancelled, now);
        Raise(new PaymentCancelled(Id, now));
    }

    private void EnsureStatus(PaymentStatus expected, string action)
    {
        if (Status != expected)
        {
            throw InvalidTransition(action);
        }
    }

    private DomainException InvalidTransition(string action) =>
        new("payment.invalid_transition", $"Cannot {action} a payment in status {Status}.");

    private void Transition(PaymentStatus next, DateTimeOffset now)
    {
        Status = next;
        UpdatedAt = now;
    }
}
