using System.Security.Cryptography;
using System.Text;

namespace PaymentOrchestrator.Infrastructure.Webhooks;

/// <summary>
/// Signs outgoing webhooks the way Stripe does: HMAC-SHA256 over "{timestamp}.{body}". Including the timestamp
/// lets merchants reject replayed requests.
/// </summary>
public static class WebhookSigner
{
    public const string IdHeader = "Webhook-Id";
    public const string TimestampHeader = "Webhook-Timestamp";
    public const string SignatureHeader = "Webhook-Signature";

    /// <summary>Named HttpClient used to deliver webhooks.</summary>
    public const string HttpClientName = "merchant-webhooks";

    public static string Sign(string secret, long timestamp, string body) =>
        "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    public static bool Verify(string secret, long timestamp, string body, string signature) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(secret, timestamp, body)),
            Encoding.ASCII.GetBytes(signature));
}
