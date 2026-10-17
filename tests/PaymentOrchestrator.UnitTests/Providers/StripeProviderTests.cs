using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.Infrastructure.Providers.Stripe;
using PaymentOrchestrator.UnitTests.Fakes;

namespace PaymentOrchestrator.UnitTests.Providers;

public class StripeProviderTests
{
    private const string WebhookSecret = "whsec_test";
    private readonly StubHttpHandler _http = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    private StripeProvider CreateProvider() =>
        new(_http.CreateClient("https://api.stripe.com/"), new StripeOptions { SecretKey = "sk_test", WebhookSecret = WebhookSecret }, _clock);

    private static AuthorizeCommand Command(bool capture = false) =>
        new(Guid.NewGuid(), new Money(2_000, "USD"), "pm_card_visa", "ada@example.com", "Order", capture);

    [Fact]
    public async Task Manual_capture_intent_maps_to_approved_not_captured()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"pi_123","status":"requires_capture"}""");

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Approved, result.Outcome);
        Assert.Equal("pi_123", result.ProviderReference);
        Assert.False(result.Captured);
    }

    [Fact]
    public async Task Sends_idempotency_key_and_manual_capture()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"pi_123","status":"requires_capture"}""");
        var command = Command();

        await CreateProvider().AuthorizeAsync(command, CancellationToken.None);

        var (request, body) = Assert.Single(_http.Requests);
        Assert.Equal($"authorize-{command.PaymentId:N}", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Contains("capture_method=manual", body);
        Assert.Contains("currency=usd", body);
    }

    [Fact]
    public async Task Card_error_maps_to_decline_code()
    {
        _http.Respond(HttpStatusCode.PaymentRequired, """{"error":{"type":"card_error","code":"card_declined","decline_code":"insufficient_funds","message":"Your card has insufficient funds."}}""");

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Declined, result.Outcome);
        Assert.Equal("insufficient_funds", result.Code);
    }

    [Fact]
    public async Task Server_error_is_unknown_outcome()
    {
        _http.Respond(HttpStatusCode.InternalServerError, "{}");

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task Rate_limit_is_unavailable()
    {
        _http.Respond(HttpStatusCode.TooManyRequests, "{}");

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Connection_failure_is_unavailable()
    {
        _http.Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public void Valid_webhook_is_parsed()
    {
        var paymentId = Guid.NewGuid();
        var body = $$$$$"""{"id":"evt_1","type":"payment_intent.succeeded","data":{"object":{"id":"pi_123","amount":2000,"currency":"usd","metadata":{"payment_id":"{{{{{paymentId}}}}}"}}}}""";

        var evt = CreateProvider().ParseWebhook(Headers(body, _clock.GetUtcNow()), body);

        Assert.Equal(ProviderWebhookEventType.PaymentCaptured, evt.Type);
        Assert.Equal(paymentId, evt.PaymentId);
        Assert.Equal("pi_123", evt.ProviderReference);
        Assert.Equal(new Money(2_000, "USD"), evt.Amount);
    }

    [Fact]
    public void Tampered_webhook_is_rejected()
    {
        const string body = """{"id":"evt_1","type":"payment_intent.succeeded","data":{"object":{"id":"pi_123"}}}""";
        var headers = Headers(body, _clock.GetUtcNow());

        Assert.Throws<WebhookSignatureException>(() => CreateProvider().ParseWebhook(headers, body.Replace("pi_123", "pi_999", StringComparison.Ordinal)));
    }

    [Fact]
    public void Replayed_old_webhook_is_rejected()
    {
        const string body = """{"id":"evt_1","type":"payment_intent.succeeded","data":{"object":{"id":"pi_123"}}}""";

        Assert.Throws<WebhookSignatureException>(() => CreateProvider().ParseWebhook(Headers(body, _clock.GetUtcNow().AddMinutes(-10)), body));
    }

    private static Dictionary<string, string> Headers(string body, DateTimeOffset signedAt)
    {
        var timestamp = signedAt.ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
        return new() { [StripeProvider.SignatureHeader] = $"t={timestamp},v1={signature}" };
    }
}
