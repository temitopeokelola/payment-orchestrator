using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;
using PaymentOrchestrator.Infrastructure.Providers.Paystack;
using PaymentOrchestrator.UnitTests.Fakes;

namespace PaymentOrchestrator.UnitTests.Providers;

public class PaystackProviderTests
{
    private const string SecretKey = "sk_test_paystack";
    private readonly StubHttpHandler _http = new();

    private PaystackProvider CreateProvider() =>
        new(_http.CreateClient("https://api.paystack.co/"), new PaystackOptions { SecretKey = SecretKey });

    private static AuthorizeCommand Command(string? email = "ada@example.com") =>
        new(Guid.NewGuid(), new Money(500_000, "NGN"), "AUTH_abc123", email, "Order", Capture: true);

    [Fact]
    public async Task Successful_charge_is_approved_and_captured()
    {
        _http.Respond(HttpStatusCode.OK, """{"status":true,"message":"Charge attempted","data":{"status":"success","reference":"ref_1","gateway_response":"Approved"}}""");

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Approved, result.Outcome);
        Assert.True(result.Captured);
    }

    [Fact]
    public async Task Uses_payment_id_as_reference()
    {
        _http.Respond(HttpStatusCode.OK, """{"status":true,"data":{"status":"success","reference":"x"}}""");
        var command = Command();

        await CreateProvider().AuthorizeAsync(command, CancellationToken.None);

        Assert.Contains($"\"reference\":\"{command.PaymentId:N}\"", _http.Requests[0].Body);
    }

    [Fact]
    public async Task Failed_charge_is_declined_with_gateway_reason()
    {
        _http.Respond(HttpStatusCode.OK, """{"status":true,"data":{"status":"failed","reference":"ref_1","gateway_response":"Insufficient Funds"}}""");

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Declined, result.Outcome);
        Assert.Equal("insufficient_funds", result.Code);
    }

    [Fact]
    public async Task Duplicate_reference_verifies_the_original_charge()
    {
        _http
            .Respond(HttpStatusCode.BadRequest, """{"status":false,"message":"Duplicate Transaction Reference"}""")
            .Respond(HttpStatusCode.OK, """{"status":true,"data":{"status":"success","reference":"ref_1"}}""");

        var result = await CreateProvider().AuthorizeAsync(Command(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.Approved, result.Outcome);
        Assert.Equal(HttpMethod.Get, _http.Requests[1].Request.Method);
    }

    [Fact]
    public async Task Missing_email_is_declined_without_calling_paystack()
    {
        var result = await CreateProvider().AuthorizeAsync(Command(email: null), CancellationToken.None);

        Assert.Equal("customer_email_required", result.Code);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void Signed_charge_success_webhook_is_parsed()
    {
        var paymentId = Guid.NewGuid();
        var body = $$$"""{"event":"charge.success","data":{"id":302961,"reference":"{{{paymentId:N}}}","amount":500000,"currency":"NGN"}}""";

        var evt = CreateProvider().ParseWebhook(Headers(body), body);

        Assert.Equal(ProviderWebhookEventType.PaymentCaptured, evt.Type);
        Assert.Equal(paymentId, evt.PaymentId);
        Assert.Equal("charge.success:302961", evt.EventId);
    }

    [Fact]
    public void Wrong_signature_is_rejected()
    {
        const string body = """{"event":"charge.success","data":{"id":1,"reference":"x"}}""";
        var headers = new Dictionary<string, string> { [PaystackProvider.SignatureHeader] = new string('a', 128) };

        Assert.Throws<WebhookSignatureException>(() => CreateProvider().ParseWebhook(headers, body));
    }

    private static Dictionary<string, string> Headers(string body) => new()
    {
        [PaystackProvider.SignatureHeader] =
            Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(body))),
    };
}
