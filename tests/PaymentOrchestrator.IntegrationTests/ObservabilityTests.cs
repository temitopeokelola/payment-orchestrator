using System.Net;
using System.Net.Http.Json;
using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class ObservabilityTests(PaymentsApiFactory factory)
{
    [Fact]
    public async Task Metrics_endpoint_exposes_business_and_provider_metrics()
    {
        var merchant = factory.CreateMerchantClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(1_000, "NGN", "Metrics", "sim_card_approved")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        (await merchant.SendAsync(request)).EnsureSuccessStatusCode();

        var metrics = await Eventually.Get(async () =>
        {
            var body = await factory.CreateClient().GetStringAsync("/metrics");
            return body.Contains("payments_created_total", StringComparison.Ordinal) ? body : null;
        });

        Assert.Contains("payments_outcomes_total", metrics);
        Assert.Contains("provider_call_duration_seconds_bucket", metrics);
        Assert.Contains("provider_circuit_state", metrics);
        Assert.Contains("http_server_request_duration_seconds", metrics);
    }

    [Fact]
    public async Task Readiness_checks_the_database()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_dependencies()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
