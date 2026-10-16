using System.Net;
using System.Net.Http.Json;
using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class PaymentsApiTests(PaymentsApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateMerchantClient();

    private static HttpRequestMessage CreatePaymentRequest(string idempotencyKey, long amount = 50_000) =>
        new(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(amount, "NGN", "Order #1001")),
            Headers = { { "Idempotency-Key", idempotencyKey } },
        };

    [Fact]
    public async Task Create_payment_returns_created_payment()
    {
        var response = await _client.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.NotNull(payment);
        Assert.Equal("created", payment.Status);
        Assert.Equal(50_000, payment.Amount);
        Assert.Equal("NGN", payment.Currency);
        Assert.Equal($"/v1/payments/{payment.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Created_payment_can_be_fetched()
    {
        var created = await (await _client.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString())))
            .Content.ReadFromJsonAsync<PaymentResponse>();

        var fetched = await _client.GetFromJsonAsync<PaymentResponse>($"/v1/payments/{created!.Id}");

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
        var response = await _client.PostAsJsonAsync("/v1/payments", new CreatePaymentRequest(1_000, "NGN", null));

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
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(1_000, "NAIRA", null)),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("money.invalid_currency", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Created_payment_can_be_cancelled_once()
    {
        var created = await (await _client.SendAsync(CreatePaymentRequest(Guid.NewGuid().ToString())))
            .Content.ReadFromJsonAsync<PaymentResponse>();

        var first = await _client.PostAsync($"/v1/payments/{created!.Id}/cancel", null);
        var second = await _client.PostAsync($"/v1/payments/{created.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Unknown_payment_returns_not_found()
    {
        var response = await _client.GetAsync($"/v1/payments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
