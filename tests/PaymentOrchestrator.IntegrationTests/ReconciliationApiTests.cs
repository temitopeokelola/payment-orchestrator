using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaymentOrchestrator.Application.Ledger;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Reconciliation;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class ReconciliationApiTests(PaymentsApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly HttpClient _merchant = factory.CreateMerchantClient();
    private readonly HttpClient _operator = factory.CreateOperatorClient();

    [Fact]
    public async Task Reconciliation_settles_payment_whose_outcome_was_lost()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(42_000, "NGN", "Order", "sim_card_timeout")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        var payment = (await (await _merchant.SendAsync(request)).Content.ReadFromJsonAsync<PaymentResponse>())!;
        Assert.Equal("created", payment.Status);

        var response = await _operator.PostAsJsonAsync("/v1/admin/reconciliation/runs", new { provider = "simulator-a", from = DateTimeOffset.UtcNow.AddHours(-1), to = DateTimeOffset.UtcNow.AddMinutes(1) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<ReconciliationRun>(Json);
        var item = Assert.Single(run!.Items, i => i.PaymentId == payment.Id);
        Assert.Equal(ReconciliationItemKind.PendingSettled, item.Kind);
        Assert.True(item.Resolved);

        var settled = await _merchant.GetFromJsonAsync<PaymentResponse>($"/v1/payments/{payment.Id}");
        Assert.Equal("captured", settled!.Status);

        var entries = await _merchant.GetFromJsonAsync<List<JournalEntryResponse>>($"/v1/payments/{payment.Id}/ledger");
        Assert.Equal("capture", Assert.Single(entries!).Type);

        var stored = await _operator.GetFromJsonAsync<ReconciliationRun>($"/v1/admin/reconciliation/runs/{run.Id}", Json);
        Assert.Equal(run.Items.Count, stored!.Items.Count);
    }

    [Fact]
    public async Task Unknown_provider_returns_not_found()
    {
        var response = await _operator.PostAsJsonAsync("/v1/admin/reconciliation/runs", new { provider = "nope" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Merchants_cannot_run_reconciliation()
    {
        var response = await _merchant.PostAsJsonAsync("/v1/admin/reconciliation/runs", new { provider = "simulator-a" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
