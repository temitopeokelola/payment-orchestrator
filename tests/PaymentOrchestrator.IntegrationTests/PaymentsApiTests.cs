using System.Net;
using System.Net.Http.Json;
using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class PaymentsApiTests(PaymentsApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateMerchantClient();

    private static HttpRequestMessage CreatePaymentRequest(
        string idempotencyKey,
        long amount = 50_000,
        string paymentMethod = "sim_card_approved",
        bool capture = true,
        string currency = "NGN") =>
        new(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(amount, currency, "Order #1001", paymentMethod, "ada@example.com", capture)),
            Headers = { { "Idempotency-Key", idempotencyKey } },
        };

    private async Task<PaymentResponse> CreatePaymentAsync(string paymentMethod = "sim_card_approved", bool capture = true, long amount = 50_000)
    {
        var response = await _client.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString(), amount, paymentMethod, capture));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
    }

    private static HttpRequestMessage RefundRequest(Guid paymentId, long amount) =>
        new(HttpMethod.Post, $"/v1/payments/{paymentId}/refunds")
        {
            Content = JsonContent.Create(new RefundPaymentRequest(amount)),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };

    [Fact]
    public async Task Approved_card_is_captured_by_cheapest_provider()
    {
        var response = await _client.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.NotNull(payment);
        Assert.Equal("captured", payment.Status);
        Assert.Equal("simulator-a", payment.Provider);
        Assert.NotNull(payment.ProviderReference);
        Assert.Equal(50_000, payment.CapturedAmount);
        Assert.Equal($"/v1/payments/{payment.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Authorize_only_payment_can_be_captured_later()
    {
        var payment = await CreatePaymentAsync(capture: false);
        Assert.Equal("authorized", payment.Status);

        var response = await _client.PostAsync($"/v1/payments/{payment.Id}/capture", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("captured", (await response.Content.ReadFromJsonAsync<PaymentResponse>())!.Status);
    }

    [Fact]
    public async Task Declined_card_fails_with_reason()
    {
        var payment = await CreatePaymentAsync("sim_card_declined");

        Assert.Equal("failed", payment.Status);
        Assert.Equal("card_declined", payment.FailureReason);
    }

    [Fact]
    public async Task Timeout_leaves_payment_pending_and_does_not_fail_over()
    {
        var payment = await CreatePaymentAsync("sim_card_timeout");

        Assert.Equal("created", payment.Status);
        Assert.Equal("simulator-a", payment.Provider);
        Assert.Null(payment.ProviderReference);
    }

    [Fact]
    public async Task Payment_fails_when_every_provider_is_unavailable()
    {
        var payment = await CreatePaymentAsync("sim_card_unavailable");

        Assert.Equal("failed", payment.Status);
        Assert.Equal("all_providers_unavailable", payment.FailureReason);
    }

    [Fact]
    public async Task Unsupported_currency_fails_routing()
    {
        var response = await _client.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString(), currency: "JPY"));
        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>();

        Assert.Equal("failed", payment!.Status);
        Assert.Equal("no_eligible_provider", payment.FailureReason);
    }

    [Fact]
    public async Task Partial_then_full_refund()
    {
        var payment = await CreatePaymentAsync(amount: 10_000);

        var partial = await _client.SendAsync(RefundRequest(payment.Id, 4_000));
        var rest = await _client.SendAsync(RefundRequest(payment.Id, 6_000));

        Assert.Equal("partially_refunded", (await partial.Content.ReadFromJsonAsync<PaymentResponse>())!.Status);
        var refunded = await rest.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.Equal("refunded", refunded!.Status);
        Assert.Equal(10_000, refunded.RefundedAmount);
    }

    [Fact]
    public async Task Refund_above_captured_amount_is_rejected()
    {
        var payment = await CreatePaymentAsync(amount: 10_000);

        var response = await _client.SendAsync(RefundRequest(payment.Id, 10_001));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("payment.refund_exceeds_capture", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Created_payment_can_be_fetched()
    {
        var created = await CreatePaymentAsync();

        var fetched = await _client.GetFromJsonAsync<PaymentResponse>($"/v1/payments/{created.Id}");

        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Request_without_api_key_is_rejected()
    {
        var anonymous = factory.CreateClient();

        var response = await anonymous.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_payment_without_idempotency_key_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/v1/payments", new CreatePaymentRequest(1_000, "NGN", null, "sim_card_approved"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Retrying_with_same_key_replays_original_response()
    {
        var key = Guid.NewGuid().ToString();

        var first = await _client.SendAsync(CreatePaymentRequest(key));
        var second = await _client.SendAsync(CreatePaymentRequest(key));

        var firstPayment = await first.Content.ReadFromJsonAsync<PaymentResponse>();
        var secondPayment = await second.Content.ReadFromJsonAsync<PaymentResponse>();

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(firstPayment!.Id, secondPayment!.Id);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Reusing_key_with_different_payload_is_rejected()
    {
        var key = Guid.NewGuid().ToString();
        await _client.SendAsync(CreatePaymentRequest(key, amount: 1_000));

        var response = await _client.SendAsync(CreatePaymentRequest(key, amount: 2_000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_with_same_key_create_a_single_payment()
    {
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _client.SendAsync(CreatePaymentRequest(key))));

        var ids = new HashSet<Guid>();
        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
        {
            ids.Add((await response.Content.ReadFromJsonAsync<PaymentResponse>())!.Id);
        }

        Assert.Single(ids);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
    }

    [Fact]
    public async Task Invalid_currency_returns_problem_details()
    {
        var response = await _client.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString(), currency: "NAIRA"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("money.invalid_currency", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorized_payment_can_be_cancelled_once()
    {
        var payment = await CreatePaymentAsync(capture: false);

        var first = await _client.PostAsync($"/v1/payments/{payment.Id}/cancel", null);
        var second = await _client.PostAsync($"/v1/payments/{payment.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Pending_payment_cannot_be_cancelled()
    {
        var payment = await CreatePaymentAsync("sim_card_timeout");

        var response = await _client.PostAsync($"/v1/payments/{payment.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("outcome_pending", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_payment_returns_not_found()
    {
        var response = await _client.GetAsync($"/v1/payments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
