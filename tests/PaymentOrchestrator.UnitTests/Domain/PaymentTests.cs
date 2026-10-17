using PaymentOrchestrator.Domain.Common;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.UnitTests.Domain;

public class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static Payment NewPayment(long amount = 10_000) =>
        Payment.Create("merchant_1", new Money(amount, "NGN"), "Order #1001", "sim_card_approved", "ada@example.com", Now);

    private static Payment CapturedPayment(long amount = 10_000)
    {
        var payment = NewPayment(amount);
        payment.Authorize("paystack", "ref_123", Now);
        payment.Capture(Now);
        return payment;
    }

    [Fact]
    public void New_payment_starts_in_created_status_and_raises_event()
    {
        var payment = NewPayment();

        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.IsType<PaymentCreated>(Assert.Single(payment.DomainEvents));
    }

    [Fact]
    public void Zero_amount_payment_is_rejected()
    {
        var ex = Assert.Throws<DomainException>(() => NewPayment(0));

        Assert.Equal("payment.zero_amount", ex.Code);
    }

    [Fact]
    public void Authorize_records_provider_details()
    {
        var payment = NewPayment();

        payment.Authorize("stripe", "pi_abc", Now);

        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.Equal("stripe", payment.Provider);
        Assert.Equal("pi_abc", payment.ProviderReference);
    }

    [Fact]
    public void Capture_requires_authorization()
    {
        var payment = NewPayment();

        var ex = Assert.Throws<DomainException>(() => payment.Capture(Now));

        Assert.Equal("payment.invalid_transition", ex.Code);
    }

    [Fact]
    public void Capture_sets_captured_amount()
    {
        var payment = CapturedPayment(25_000);

        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal(new Money(25_000, "NGN"), payment.CapturedAmount);
    }

    [Fact]
    public void Partial_refund_moves_to_partially_refunded()
    {
        var payment = CapturedPayment(10_000);

        payment.Refund(new Money(4_000, "NGN"), Now);

        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(new Money(4_000, "NGN"), payment.RefundedAmount);
    }

    [Fact]
    public void Refunds_adding_up_to_capture_move_to_refunded()
    {
        var payment = CapturedPayment(10_000);

        payment.Refund(new Money(4_000, "NGN"), Now);
        payment.Refund(new Money(6_000, "NGN"), Now);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }

    [Fact]
    public void Refund_cannot_exceed_captured_amount()
    {
        var payment = CapturedPayment(10_000);
        payment.Refund(new Money(9_000, "NGN"), Now);

        var ex = Assert.Throws<DomainException>(() => payment.Refund(new Money(2_000, "NGN"), Now));

        Assert.Equal("payment.refund_exceeds_capture", ex.Code);
        Assert.Equal(new Money(9_000, "NGN"), payment.RefundedAmount);
    }

    [Fact]
    public void Refund_in_another_currency_is_rejected()
    {
        var payment = CapturedPayment();

        var ex = Assert.Throws<DomainException>(() => payment.Refund(new Money(100, "USD"), Now));

        Assert.Equal("money.currency_mismatch", ex.Code);
    }

    [Fact]
    public void Failed_payment_cannot_be_authorized()
    {
        var payment = NewPayment();
        payment.Fail("card_declined", Now);

        Assert.Throws<DomainException>(() => payment.Authorize("stripe", "pi_abc", Now));
        Assert.Equal("card_declined", payment.FailureReason);
    }

    [Fact]
    public void Captured_payment_cannot_be_cancelled()
    {
        var payment = CapturedPayment();

        Assert.Throws<DomainException>(() => payment.Cancel(Now));
    }

    [Fact]
    public void Authorized_payment_can_be_cancelled()
    {
        var payment = NewPayment();
        payment.Authorize("stripe", "pi_abc", Now);

        payment.Cancel(Now);

        Assert.Equal(PaymentStatus.Cancelled, payment.Status);
    }

    [Fact]
    public void Payment_method_is_required()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Payment.Create("merchant_1", new Money(1_000, "NGN"), "Order", " ", null, Now));

        Assert.Equal("payment.payment_method_required", ex.Code);
    }

    [Fact]
    public void Provider_can_be_assigned_before_outcome_is_known()
    {
        var payment = NewPayment();

        payment.AssignProvider("simulator-a", Now);

        Assert.Equal("simulator-a", payment.Provider);
        Assert.Equal(PaymentStatus.Created, payment.Status);
    }

    [Fact]
    public void Refund_validation_does_not_change_state()
    {
        var payment = CapturedPayment(10_000);

        payment.EnsureCanRefund(new Money(5_000, "NGN"));

        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal(Money.Zero("NGN"), payment.RefundedAmount);
    }
}
