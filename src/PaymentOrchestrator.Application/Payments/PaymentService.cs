using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Payments;

public sealed partial class PaymentService(
    IPaymentRepository payments,
    IUnitOfWork unitOfWork,
    IPaymentRouter router,
    IProviderRegistry providers,
    IPaymentAttemptLog attempts,
    TimeProvider clock,
    ILogger<PaymentService> logger)
{
    public async Task<PaymentResponse> CreateAsync(string merchantId, CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = Payment.Create(
            merchantId,
            new Money(request.Amount, request.Currency),
            request.Description ?? string.Empty,
            request.PaymentMethod,
            request.CustomerEmail,
            clock.GetUtcNow());

        Telemetry.PaymentsCreated.Add(1, new KeyValuePair<string, object?>("currency", payment.Amount.Currency));

        // Persist before calling out, so the payment exists even if we crash mid-request.
        payments.Add(payment);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var candidates = router.Rank(new RoutingRequest(payment.Amount, !request.Capture, request.Provider));
        if (candidates.Count == 0)
        {
            payment.Fail("no_eligible_provider", clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return PaymentResponse.From(payment);
        }

        await AuthorizeWithFailoverAsync(payment, candidates, request.Capture, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        Telemetry.PaymentOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("provider", payment.Provider ?? "none"),
            new KeyValuePair<string, object?>("status", PaymentResponse.From(payment).Status));

        return PaymentResponse.From(payment);
    }

    private async Task AuthorizeWithFailoverAsync(
        Payment payment,
        IReadOnlyList<IPaymentProvider> candidates,
        bool capture,
        CancellationToken cancellationToken)
    {
        var command = new AuthorizeCommand(
            payment.Id, payment.Amount, payment.PaymentMethod, payment.CustomerEmail, payment.Description, capture);

        foreach (var provider in candidates)
        {
            payment.AssignProvider(provider.Name, clock.GetUtcNow());
            var result = await CallAsync(payment, provider, "authorize", ct => provider.AuthorizeAsync(command, ct), cancellationToken);

            switch (result.Outcome)
            {
                case ProviderOutcome.Approved:
                    payment.Authorize(provider.Name, result.ProviderReference!, clock.GetUtcNow());
                    if (result.Captured)
                    {
                        payment.Capture(clock.GetUtcNow());
                    }
                    else if (capture)
                    {
                        await TryCaptureAfterAuthorizeAsync(payment, provider, cancellationToken);
                    }

                    return;

                case ProviderOutcome.Declined:
                    payment.Fail(result.Code ?? "declined", clock.GetUtcNow());
                    return;

                case ProviderOutcome.Unknown:
                    // The provider may have charged the customer. Failing over could charge twice, so leave the
                    // payment in "created" and let the provider webhook or reconciliation settle it.
                    LogUnknownOutcome(payment.Id, provider.Name, result.Code);
                    return;

                case ProviderOutcome.Unavailable:
                    LogFailover(payment.Id, provider.Name, result.Code);
                    continue;
            }
        }

        payment.Fail("all_providers_unavailable", clock.GetUtcNow());
    }

    private async Task TryCaptureAfterAuthorizeAsync(Payment payment, IPaymentProvider provider, CancellationToken cancellationToken)
    {
        var result = await CallAsync(payment, provider, "capture", ct => provider.CaptureAsync(payment.Id, payment.ProviderReference!, payment.Amount, ct), cancellationToken);
        if (result.Outcome == ProviderOutcome.Approved)
        {
            payment.Capture(clock.GetUtcNow());
        }
        else
        {
            // Authorized funds are still held; the merchant can retry capture explicitly.
            LogCaptureFailed(payment.Id, provider.Name, result.Code);
        }
    }

    public async Task<PaymentResponse?> GetAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(paymentId, merchantId, cancellationToken);
        return payment is null ? null : PaymentResponse.From(payment);
    }

    public Task<PaymentResponse?> CaptureAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken) =>
        MutateAsync(merchantId, paymentId, async (payment, provider, ct) =>
        {
            if (payment.Status != PaymentStatus.Authorized)
            {
                payment.Capture(clock.GetUtcNow()); // throws the domain's invalid transition error
            }

            EnsureApproved(await CallAsync(payment, provider, "capture", c => provider.CaptureAsync(payment.Id, payment.ProviderReference!, payment.Amount, c), ct));
            payment.Capture(clock.GetUtcNow());
        }, cancellationToken);

    public Task<PaymentResponse?> CancelAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken) =>
        MutateAsync(merchantId, paymentId, async (payment, provider, ct) =>
        {
            if (payment.Status == PaymentStatus.Created && payment.Provider is not null)
            {
                // A provider attempt ended with an unknown outcome; the customer may already have been charged.
                throw new ProviderOperationException("outcome_pending", "The provider outcome is still pending. Retry once it settles.", retryable: true);
            }

            if (payment.Status == PaymentStatus.Authorized)
            {
                if (!provider.Capabilities.SupportsCancel)
                {
                    throw new ProviderOperationException("cancel_not_supported", $"{provider.Name} does not support cancelling authorizations.", retryable: false);
                }

                EnsureApproved(await CallAsync(payment, provider, "cancel", c => provider.CancelAsync(payment.Id, payment.ProviderReference!, c), ct));
            }

            payment.Cancel(clock.GetUtcNow());
        }, cancellationToken);

    public Task<PaymentResponse?> RefundAsync(string merchantId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken) =>
        MutateAsync(merchantId, paymentId, async (payment, provider, ct) =>
        {
            var amount = new Money(request.Amount, payment.Amount.Currency);
            payment.EnsureCanRefund(amount);

            // Deterministic per refund step, so a retried request reuses the provider's idempotency.
            var refundKey = $"{payment.Id:N}-refund-{payment.RefundedAmount.AmountMinor}";
            EnsureApproved(await CallAsync(payment, provider, "refund", c => provider.RefundAsync(payment.Id, payment.ProviderReference!, amount, refundKey, c), ct));

            payment.Refund(amount, clock.GetUtcNow());
        }, cancellationToken);

    private async Task<PaymentResponse?> MutateAsync(
        string merchantId,
        Guid paymentId,
        Func<Payment, IPaymentProvider, CancellationToken, Task> change,
        CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(paymentId, merchantId, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        var provider = payment.Provider is null ? null : providers.Find(payment.Provider);
        try
        {
            // A payment that never reached a provider (for example one that failed routing) only has local changes.
            await change(payment, provider ?? NullProvider.Instance, cancellationToken);
        }
        catch (ProviderOperationException)
        {
            // Keep the record of the failed provider call; the payment itself was not changed.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PaymentResponse.From(payment);
    }

    private async Task<ProviderResult> CallAsync(
        Payment payment,
        IPaymentProvider provider,
        string operation,
        Func<CancellationToken, Task<ProviderResult>> call,
        CancellationToken cancellationToken)
    {
        using var activity = Telemetry.ActivitySource.StartActivity($"provider.{operation}", ActivityKind.Client);
        activity?.SetTag("payment.id", payment.Id);
        activity?.SetTag("payment.provider", provider.Name);

        var started = Stopwatch.GetTimestamp();
        var result = await call(cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);

        var outcome = result.Outcome.ToString().ToLowerInvariant();
        activity?.SetTag("payment.provider.outcome", outcome);
        activity?.SetTag("payment.provider.code", result.Code);
        if (result.Outcome is ProviderOutcome.Unavailable or ProviderOutcome.Unknown)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Code);
        }

        var tags = new TagList { { "provider", provider.Name }, { "operation", operation }, { "outcome", outcome } };
        Telemetry.ProviderCalls.Add(1, tags);
        Telemetry.ProviderCallDuration.Record(elapsed.TotalSeconds, tags);

        attempts.Record(new PaymentAttempt(
            payment.Id,
            provider.Name,
            operation,
            outcome,
            result.Code,
            Math.Round(elapsed.TotalMilliseconds, 1),
            clock.GetUtcNow()));

        return result;
    }

    public async Task<IReadOnlyList<PaymentAttempt>?> GetAttemptsAsync(string merchantId, Guid paymentId, CancellationToken cancellationToken) =>
        await payments.GetAsync(paymentId, merchantId, cancellationToken) is null
            ? null
            : await attempts.ListAsync(paymentId, cancellationToken);

    private static void EnsureApproved(ProviderResult result)
    {
        switch (result.Outcome)
        {
            case ProviderOutcome.Approved:
                return;
            case ProviderOutcome.Declined:
                throw new ProviderOperationException(result.Code ?? "provider_declined", result.Message ?? "The provider declined the operation.", retryable: false);
            default:
                throw new ProviderOperationException(result.Code ?? "provider_unavailable", result.Message ?? "The provider could not complete the operation. Retry later.", retryable: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider {Provider} unavailable for payment {PaymentId} ({Code}), failing over")]
    private partial void LogFailover(Guid paymentId, string provider, string? code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown outcome from {Provider} for payment {PaymentId} ({Code}); awaiting webhook or reconciliation")]
    private partial void LogUnknownOutcome(Guid paymentId, string provider, string? code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capture after authorization failed at {Provider} for payment {PaymentId} ({Code})")]
    private partial void LogCaptureFailed(Guid paymentId, string provider, string? code);
}
