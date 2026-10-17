using System.Net;
using System.Net.Http.Json;
using PaymentOrchestrator.Application.Incidents;
using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class IncidentSummaryApiTests(PaymentsApiFactory factory)
{
    [Fact]
    public async Task Summary_includes_recent_provider_failures()
    {
        var merchant = factory.CreateMerchantClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(2_000, "NGN", "Order", "sim_card_unavailable")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        (await merchant.SendAsync(request)).EnsureSuccessStatusCode();

        var summary = await factory.CreateOperatorClient().GetFromJsonAsync<IncidentSummary>("/v1/admin/incidents/summary?minutes=30");

        // No API key in tests, so the deterministic summary is used.
        Assert.Equal("rules", summary!.GeneratedBy);
        Assert.Contains(summary.Snapshot.Attempts, a => a.Outcome == "unavailable" && a.Code == "provider_unavailable");
        Assert.Contains("provider_unavailable", summary.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_requires_operator_key()
    {
        var response = await factory.CreateMerchantClient().GetAsync("/v1/admin/incidents/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
