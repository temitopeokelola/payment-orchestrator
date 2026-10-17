using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Infrastructure.Providers.Paystack;

public sealed class PaystackOptions
{
    public string? SecretKey { get; set; }
    public int FeeBasisPoints { get; set; } = 150;
    public List<string> Currencies { get; set; } = [];
}

/// <summary>
/// Paystack adapter that charges saved authorizations (<c>AUTH_...</c> codes). Paystack captures on charge,
/// so it is only routed payments that capture immediately.
/// </summary>
public sealed class PaystackProvider(HttpClient http, PaystackOptions options) : IPaymentProvider
{
    public const string ProviderName = "paystack";
    public const string SignatureHeader = "x-paystack-signature";

    public string Name => ProviderName;

    public ProviderCapabilities Capabilities { get; } = new(
        (options.Currencies.Count > 0 ? options.Currencies : ["NGN", "GHS", "ZAR", "KES", "USD"]).Select(c => c.ToUpperInvariant()).ToHashSet(),
        SupportsSeparateCapture: false,
        SupportsCancel: false,
        options.FeeBasisPoints);

    /// <summary>Our payment id doubles as the Paystack reference, which makes charges idempotent.</summary>
    public static string ReferenceFor(Guid paymentId) => paymentId.ToString("N");

    public async Task<ProviderResult> AuthorizeAsync(AuthorizeCommand command, CancellationToken cancellationToken)
    {
        if (command.CustomerEmail is null)
        {
            return ProviderResult.Declined("customer_email_required", "Paystack requires the customer's email.");
        }

        var reference = ReferenceFor(command.PaymentId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "transaction/charge_authorization")
        {
            Content = JsonContent.Create(new
            {
                authorization_code = command.PaymentMethod,
                email = command.CustomerEmail,
                amount = command.Amount.AmountMinor,
                currency = command.Amount.Currency,
                reference,
                metadata = new { payment_id = command.PaymentId },
            }),
        };

        var result = await SendAsync(request, MapCharge, cancellationToken);

        // A retry after a lost response hits "duplicate reference": ask Paystack what actually happened.
        return result.Code == "duplicate_reference" ? await VerifyAsync(reference, cancellationToken) : result;
    }

    public Task<ProviderResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult.Approved(providerReference, captured: true));

    public Task<ProviderResult> RefundAsync(Guid paymentId, string providerReference, Money amount, string refundKey, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "refund")
        {
            Content = JsonContent.Create(new
            {
                transaction = providerReference,
                amount = amount.AmountMinor,
                merchant_note = refundKey,
            }),
        };

        return SendAsync(request, data => ProviderResult.Approved(data.GetProperty("id").ToString()), cancellationToken, dispose: request);
    }

    public Task<ProviderResult> CancelAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult.Declined("not_supported", "Paystack charges cannot be cancelled; refund instead."));

    public ProviderWebhookEvent ParseWebhook(IReadOnlyDictionary<string, string> headers, string body)
    {
        if (options.SecretKey is null || !headers.TryGetValue(SignatureHeader, out var signature))
        {
            throw new WebhookSignatureException("Missing Paystack signature.");
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA512.HashData(Encoding.UTF8.GetBytes(options.SecretKey), Encoding.UTF8.GetBytes(body)));

        if (!ProviderHttp.FixedTimeEqualsHex(expected, signature))
        {
            throw new WebhookSignatureException("Paystack signature mismatch.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var eventName = root.GetProperty("event").GetString();
        var data = root.GetProperty("data");

        var type = eventName switch
        {
            "charge.success" => ProviderWebhookEventType.PaymentCaptured,
            "refund.processed" => ProviderWebhookEventType.RefundSucceeded,
            _ => ProviderWebhookEventType.Ignored,
        };

        var reference = type == ProviderWebhookEventType.RefundSucceeded
            ? data.TryGetProperty("transaction_reference", out var tr) ? tr.GetString() : null
            : data.TryGetProperty("reference", out var r) ? r.GetString() : null;

        Guid? paymentId = Guid.TryParseExact(reference, "N", out var parsed) ? parsed : null;

        Money? amount = data.TryGetProperty("amount", out var a) && data.TryGetProperty("currency", out var c)
            ? new Money(a.GetInt64(), c.GetString()!)
            : null;

        // Paystack events have no id of their own; event name plus the object's id is unique.
        var eventId = $"{eventName}:{(data.TryGetProperty("id", out var id) ? id.ToString() : reference)}";
        return new ProviderWebhookEvent(eventId, type, paymentId, reference, amount);
    }

    private async Task<ProviderResult> VerifyAsync(string reference, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"transaction/verify/{Uri.EscapeDataString(reference)}");
        return await SendAsync(request, MapCharge, cancellationToken);
    }

    private async Task<ProviderResult> SendAsync(
        HttpRequestMessage request,
        Func<JsonElement, ProviderResult> map,
        CancellationToken cancellationToken,
        IDisposable? dispose = null)
    {
        using (dispose)
        {
            var (response, failure) = await ProviderHttp.SendAsync(http, request, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            using (response)
            {
                if ((int)response!.StatusCode >= 500 || (int)response.StatusCode == 429)
                {
                    return ProviderHttp.FromServerError(response.StatusCode);
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;

                if (!response.IsSuccessStatusCode || !root.GetProperty("status").GetBoolean())
                {
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var code = message?.Contains("Duplicate Transaction Reference", StringComparison.OrdinalIgnoreCase) == true
                        ? "duplicate_reference"
                        : "paystack_error";
                    return ProviderResult.Declined(code, message);
                }

                return map(root.GetProperty("data"));
            }
        }
    }

    private static ProviderResult MapCharge(JsonElement data)
    {
        var reference = data.GetProperty("reference").GetString()!;
        var gatewayResponse = data.TryGetProperty("gateway_response", out var g) ? g.GetString() : null;

        return data.GetProperty("status").GetString() switch
        {
            "success" => ProviderResult.Approved(reference, captured: true),
            "failed" or "reversed" or "abandoned" => ProviderResult.Declined(
                gatewayResponse?.ToLowerInvariant().Replace(' ', '_') ?? "declined", gatewayResponse),
            "send_otp" or "send_pin" or "open_url" => ProviderResult.Declined("authentication_required", gatewayResponse),
            _ => ProviderResult.Unknown("pending", gatewayResponse),
        };
    }
}
