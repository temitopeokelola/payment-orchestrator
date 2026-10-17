using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Infrastructure.Providers.Stripe;

public sealed class StripeOptions
{
    public string? SecretKey { get; set; }
    public string? WebhookSecret { get; set; }
    public int FeeBasisPoints { get; set; } = 290;
    public List<string> Currencies { get; set; } = [];
    public int WebhookToleranceSeconds { get; set; } = 300;
}

/// <summary>Stripe PaymentIntents adapter using manual or automatic capture.</summary>
public sealed class StripeProvider(HttpClient http, StripeOptions options, TimeProvider clock) : IPaymentProvider
{
    public const string ProviderName = "stripe";
    public const string SignatureHeader = "Stripe-Signature";

    public string Name => ProviderName;

    public ProviderCapabilities Capabilities { get; } = new(
        (options.Currencies.Count > 0 ? options.Currencies : ["USD", "GBP", "EUR"]).Select(c => c.ToUpperInvariant()).ToHashSet(),
        SupportsSeparateCapture: true,
        SupportsCancel: true,
        options.FeeBasisPoints);

    public Task<ProviderResult> AuthorizeAsync(AuthorizeCommand command, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["amount"] = command.Amount.AmountMinor.ToString(CultureInfo.InvariantCulture),
            ["currency"] = command.Amount.Currency.ToLowerInvariant(),
            ["payment_method"] = command.PaymentMethod,
            ["payment_method_types[]"] = "card",
            ["confirm"] = "true",
            ["capture_method"] = command.Capture ? "automatic" : "manual",
            ["description"] = command.Description,
            ["metadata[payment_id]"] = command.PaymentId.ToString(),
        };

        if (command.CustomerEmail is not null)
        {
            form["receipt_email"] = command.CustomerEmail;
        }

        return PostAsync("v1/payment_intents", form, $"authorize-{command.PaymentId:N}", MapPaymentIntent, cancellationToken);
    }

    public Task<ProviderResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) =>
        PostAsync(
            $"v1/payment_intents/{Uri.EscapeDataString(providerReference)}/capture",
            new() { ["amount_to_capture"] = amount.AmountMinor.ToString(CultureInfo.InvariantCulture) },
            $"capture-{paymentId:N}",
            MapPaymentIntent,
            cancellationToken);

    public Task<ProviderResult> RefundAsync(Guid paymentId, string providerReference, Money amount, string refundKey, CancellationToken cancellationToken) =>
        PostAsync(
            "v1/refunds",
            new()
            {
                ["payment_intent"] = providerReference,
                ["amount"] = amount.AmountMinor.ToString(CultureInfo.InvariantCulture),
                ["metadata[payment_id]"] = paymentId.ToString(),
            },
            refundKey,
            root => root.GetProperty("status").GetString() is "succeeded" or "pending"
                ? ProviderResult.Approved(root.GetProperty("id").GetString()!)
                : ProviderResult.Declined("refund_failed"),
            cancellationToken);

    public Task<ProviderResult> CancelAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken) =>
        PostAsync(
            $"v1/payment_intents/{Uri.EscapeDataString(providerReference)}/cancel",
            [],
            $"cancel-{paymentId:N}",
            root => root.GetProperty("status").GetString() == "canceled"
                ? ProviderResult.Approved(root.GetProperty("id").GetString()!)
                : ProviderResult.Declined("cancel_failed"),
            cancellationToken);

    public ProviderWebhookEvent ParseWebhook(IReadOnlyDictionary<string, string> headers, string body)
    {
        if (options.WebhookSecret is null || !headers.TryGetValue(SignatureHeader, out var header))
        {
            throw new WebhookSignatureException("Missing Stripe signature.");
        }

        VerifySignature(header, body, options.WebhookSecret);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var data = root.GetProperty("data").GetProperty("object");

        var type = root.GetProperty("type").GetString() switch
        {
            "payment_intent.amount_capturable_updated" => ProviderWebhookEventType.PaymentAuthorized,
            "payment_intent.succeeded" => ProviderWebhookEventType.PaymentCaptured,
            "payment_intent.payment_failed" => ProviderWebhookEventType.PaymentFailed,
            "charge.refunded" => ProviderWebhookEventType.RefundSucceeded,
            _ => ProviderWebhookEventType.Ignored,
        };

        var reference = type == ProviderWebhookEventType.RefundSucceeded
            ? data.TryGetProperty("payment_intent", out var pi) ? pi.GetString() : null
            : data.TryGetProperty("id", out var id) ? id.GetString() : null;

        Guid? paymentId = data.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("payment_id", out var pid)
            && Guid.TryParse(pid.GetString(), out var parsed) ? parsed : null;

        Money? amount = data.TryGetProperty("amount", out var a) && data.TryGetProperty("currency", out var c)
            ? new Money(a.GetInt64(), c.GetString()!)
            : null;

        var failureCode = data.TryGetProperty("last_payment_error", out var error) && error.ValueKind == JsonValueKind.Object
            ? (error.TryGetProperty("decline_code", out var dc) ? dc.GetString() : null) ?? error.GetProperty("code").GetString()
            : null;

        return new ProviderWebhookEvent(root.GetProperty("id").GetString()!, type, paymentId, reference, amount, failureCode);
    }

    private void VerifySignature(string header, string body, string secret)
    {
        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0] == "t")
            {
                timestamp = kv[1];
            }
            else if (kv[0] == "v1")
            {
                signatures.Add(kv[1]);
            }
        }

        if (timestamp is null || signatures.Count == 0 || !long.TryParse(timestamp, out var unix))
        {
            throw new WebhookSignatureException("Malformed Stripe signature header.");
        }

        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unix);
        if (age.Duration() > TimeSpan.FromSeconds(options.WebhookToleranceSeconds))
        {
            throw new WebhookSignatureException("Stripe signature timestamp outside tolerance.");
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

        if (!signatures.Any(s => ProviderHttp.FixedTimeEqualsHex(expected, s)))
        {
            throw new WebhookSignatureException("Stripe signature mismatch.");
        }
    }

    private async Task<ProviderResult> PostAsync(
        string path,
        Dictionary<string, string> form,
        string idempotencyKey,
        Func<JsonElement, ProviderResult> map,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var (response, failure) = await ProviderHttp.SendAsync(http, request, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        using (response)
        {
            if ((int)response!.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ProviderHttp.FromServerError(response.StatusCode);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode)
            {
                var error = root.GetProperty("error");
                var code = (error.TryGetProperty("decline_code", out var dc) ? dc.GetString() : null)
                    ?? (error.TryGetProperty("code", out var c) ? c.GetString() : null)
                    ?? $"http_{(int)response.StatusCode}";
                return ProviderResult.Declined(code, error.TryGetProperty("message", out var m) ? m.GetString() : null);
            }

            return map(root);
        }
    }

    private static ProviderResult MapPaymentIntent(JsonElement intent)
    {
        var id = intent.GetProperty("id").GetString()!;
        return intent.GetProperty("status").GetString() switch
        {
            "requires_capture" => ProviderResult.Approved(id),
            "succeeded" => ProviderResult.Approved(id, captured: true),
            "processing" => ProviderResult.Unknown("processing", "Stripe is still processing the payment."),
            "requires_action" => ProviderResult.Declined("authentication_required", "The card requires customer authentication."),
            var status => ProviderResult.Declined(status ?? "unknown_status"),
        };
    }
}
