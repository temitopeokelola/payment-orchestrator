using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Infrastructure.Providers.Simulator;

/// <summary>
/// A fake provider that behaves like a real one: idempotent operations, declines, timeouts and outages.
/// Test tokens:
/// <list type="bullet">
/// <item><c>sim_card_approved</c>: approved.</item>
/// <item><c>sim_card_declined</c>, <c>sim_card_insufficient_funds</c>: declined.</item>
/// <item><c>sim_card_timeout</c>: the charge succeeds but the response is lost (unknown outcome).</item>
/// <item><c>sim_card_unavailable</c>: refused before processing (safe to fail over).</item>
/// </list>
/// </summary>
public sealed class SimulatorProvider(SimulatorOptions options, SimulatorLedger ledger, TimeProvider clock) : IPaymentProvider
{
    public const string SignatureHeader = "X-Simulator-Signature";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Name => options.Name;

    public ProviderCapabilities Capabilities { get; } = new(
        options.Currencies.Select(c => c.ToUpperInvariant()).ToHashSet(),
        SupportsSeparateCapture: true,
        SupportsCancel: true,
        options.FeeBasisPoints);

    public async Task<ProviderResult> AuthorizeAsync(AuthorizeCommand command, CancellationToken cancellationToken)
    {
        if (await RefuseAsync(cancellationToken) is { } refused)
        {
            return refused;
        }

        switch (command.PaymentMethod)
        {
            case "sim_card_declined":
                return ProviderResult.Declined("card_declined", "The card was declined.");
            case "sim_card_insufficient_funds":
                return ProviderResult.Declined("insufficient_funds", "The card has insufficient funds.");
            case "sim_card_unavailable":
                return ProviderResult.Unavailable("provider_unavailable", "The provider refused the connection.");
            case "sim_card_timeout":
                // The money moves, but we never hear back. Only a webhook or reconciliation can tell us.
                Record(command, captured: true);
                return ProviderResult.Unknown("timeout", "The provider did not respond in time.");
            case "sim_card_approved":
                var transaction = Record(command, command.Capture);
                return ProviderResult.Approved(transaction.Reference, transaction.Status == SimulatedStatus.Captured);
            default:
                return ProviderResult.Declined("invalid_payment_method", "Unknown payment method token.");
        }
    }

    public async Task<ProviderResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken)
    {
        if (await RefuseAsync(cancellationToken) is { } refused)
        {
            return refused;
        }

        var transaction = ledger.Find(providerReference);
        if (transaction is null)
        {
            return ProviderResult.Declined("not_found");
        }

        lock (transaction)
        {
            if (transaction.Status == SimulatedStatus.Cancelled)
            {
                return ProviderResult.Declined("authorization_cancelled");
            }

            transaction.Status = SimulatedStatus.Captured;
            return ProviderResult.Approved(transaction.Reference, captured: true);
        }
    }

    public async Task<ProviderResult> RefundAsync(Guid paymentId, string providerReference, Money amount, string refundKey, CancellationToken cancellationToken)
    {
        if (await RefuseAsync(cancellationToken) is { } refused)
        {
            return refused;
        }

        var transaction = ledger.Find(providerReference);
        if (transaction is null)
        {
            return ProviderResult.Declined("not_found");
        }

        lock (transaction)
        {
            if (transaction.RefundKeys.Contains(refundKey))
            {
                return ProviderResult.Approved($"{transaction.Reference}_{refundKey}");
            }

            if (transaction.Status != SimulatedStatus.Captured || transaction.RefundedMinor + amount.AmountMinor > transaction.Amount.AmountMinor)
            {
                return ProviderResult.Declined("refund_not_allowed");
            }

            transaction.RefundKeys.Add(refundKey);
            transaction.RefundedMinor += amount.AmountMinor;
            return ProviderResult.Approved($"{transaction.Reference}_{refundKey}");
        }
    }

    public async Task<ProviderResult> CancelAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken)
    {
        if (await RefuseAsync(cancellationToken) is { } refused)
        {
            return refused;
        }

        var transaction = ledger.Find(providerReference);
        if (transaction is null)
        {
            return ProviderResult.Declined("not_found");
        }

        lock (transaction)
        {
            if (transaction.Status == SimulatedStatus.Captured)
            {
                return ProviderResult.Declined("already_captured");
            }

            transaction.Status = SimulatedStatus.Cancelled;
            return ProviderResult.Approved(transaction.Reference);
        }
    }

    public ProviderWebhookEvent ParseWebhook(IReadOnlyDictionary<string, string> headers, string body)
    {
        if (!headers.TryGetValue(SignatureHeader, out var signature) || !IsValidSignature(body, signature))
        {
            throw new WebhookSignatureException("Missing or invalid simulator signature.");
        }

        var payload = JsonSerializer.Deserialize<SimulatorWebhookPayload>(body, JsonOptions)
            ?? throw new WebhookSignatureException("Empty webhook payload.");

        var type = payload.Type switch
        {
            "payment.authorized" => ProviderWebhookEventType.PaymentAuthorized,
            "payment.captured" => ProviderWebhookEventType.PaymentCaptured,
            "payment.failed" => ProviderWebhookEventType.PaymentFailed,
            "refund.succeeded" => ProviderWebhookEventType.RefundSucceeded,
            _ => ProviderWebhookEventType.Ignored,
        };

        Money? amount = payload.Amount is { } minor && payload.Currency is { } currency ? new Money(minor, currency) : null;
        return new ProviderWebhookEvent(payload.Id, type, payload.PaymentId, payload.Reference, amount, payload.FailureCode);
    }

    /// <summary>Builds a signed webhook exactly as the simulator would send it. Used by tests and the dev endpoints.</summary>
    public (string Body, string Signature) CreateWebhook(string type, SimulatedTransaction transaction, string? failureCode = null)
    {
        var body = JsonSerializer.Serialize(
            new SimulatorWebhookPayload(
                $"evt_{Guid.NewGuid():N}",
                type,
                transaction.PaymentId,
                transaction.Reference,
                transaction.Amount.AmountMinor,
                transaction.Amount.Currency,
                failureCode),
            JsonOptions);

        return (body, Sign(body));
    }

    private SimulatedTransaction Record(AuthorizeCommand command, bool captured) =>
        ledger.GetOrAdd(Name, command.PaymentId, () => new SimulatedTransaction
        {
            Provider = Name,
            Reference = $"{Name}_{Guid.NewGuid():N}",
            PaymentId = command.PaymentId,
            Amount = command.Amount,
            CreatedAt = clock.GetUtcNow(),
            Status = captured ? SimulatedStatus.Captured : SimulatedStatus.Authorized,
        });

    private async Task<ProviderResult?> RefuseAsync(CancellationToken cancellationToken)
    {
        if (options.LatencyMs > 0)
        {
            var jitter = Random.Shared.Next(0, Math.Max(1, options.LatencyMs / 2));
            await Task.Delay(TimeSpan.FromMilliseconds(options.LatencyMs + jitter), clock, cancellationToken);
        }

        if (ledger.IsDown(Name))
        {
            return ProviderResult.Unavailable("provider_outage", $"{Name} is down.");
        }

        return options.FailureRate > 0 && Random.Shared.NextDouble() < options.FailureRate
            ? ProviderResult.Unavailable("provider_error", $"{Name} returned 503.")
            : null;
    }

    private string Sign(string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.WebhookSecret), Encoding.UTF8.GetBytes(body)));

    private bool IsValidSignature(string body, string signature)
    {
        var expected = Encoding.UTF8.GetBytes(Sign(body));
        var actual = Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private sealed record SimulatorWebhookPayload(
        string Id,
        string Type,
        Guid? PaymentId,
        string? Reference,
        long? Amount,
        string? Currency,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureCode);
}
