using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Infrastructure.Providers.Simulator;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class WebhooksApiTests(PaymentsApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateMerchantClient();

    private async Task<PaymentResponse> CreatePaymentAsync(string paymentMethod)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(25_000, "NGN", "Order", paymentMethod, "ada@example.com")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
    }

    private Task<HttpResponseMessage> SendSimulatorWebhookAsync(Guid paymentId, string type) =>
        _client.PostAsync($"/dev/simulators/simulator-a/payments/{paymentId}/webhooks/{type}", null);

    [Fact]
    public async Task Webhook_settles_payment_whose_authorization_timed_out()
    {
        var payment = await CreatePaymentAsync("sim_card_timeout");
        Assert.Equal("created", payment.Status);

        var response = await SendSimulatorWebhookAsync(payment.Id, "payment.captured");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settled = await _client.GetFromJsonAsync<PaymentResponse>($"/v1/payments/{payment.Id}");
        Assert.Equal("captured", settled!.Status);
        Assert.NotNull(settled.ProviderReference);
    }

    [Fact]
    public async Task Same_webhook_delivered_twice_is_applied_once()
    {
        var payment = await CreatePaymentAsync("sim_card_timeout");
        var simulator = factory.Services.GetServices<SimulatorProvider>().Single(s => s.Name == "simulator-a");
        var transaction = factory.Services.GetRequiredService<SimulatorLedger>().Transactions("simulator-a").Single(t => t.PaymentId == payment.Id);
        var (body, signature) = simulator.CreateWebhook("payment.captured", transaction);

        var first = await PostWebhookAsync(body, signature);
        var second = await PostWebhookAsync(body, signature);

        Assert.Contains("applied", await first.Content.ReadAsStringAsync());
        Assert.Contains("duplicate", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Webhook_with_bad_signature_is_rejected()
    {
        var response = await PostWebhookAsync("""{"id":"evt_forged","type":"payment.captured"}""", new string('0', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_for_unknown_provider_returns_not_found()
    {
        var response = await factory.CreateClient().PostAsync("/webhooks/nope", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Outage_on_primary_provider_fails_over_to_secondary()
    {
        var ledger = factory.Services.GetRequiredService<SimulatorLedger>();
        ledger.SetOutage("simulator-a", true);
        try
        {
            var payment = await CreatePaymentAsync("sim_card_approved");

            Assert.Equal("captured", payment.Status);
            Assert.Equal("simulator-b", payment.Provider);
        }
        finally
        {
            ledger.SetOutage("simulator-a", false);
        }
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/simulator-a")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SimulatorProvider.SignatureHeader, signature);
        return factory.CreateClient().SendAsync(request);
    }
}
