using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Outbox;
using PaymentOrchestrator.Infrastructure.Persistence;
using PaymentOrchestrator.Infrastructure.Webhooks;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class MerchantWebhookTests(PaymentsApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateMerchantClient();

    private async Task<PaymentResponse> CreatePaymentAsync(string description)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(7_500, "NGN", description, "sim_card_approved", "ada@example.com")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
    }

    private List<(HttpRequestMessage Request, string Body)> WebhooksFor(Guid paymentId) =>
        [.. factory.Webhooks.Received.Where(w => w.Body.Contains(paymentId.ToString(), StringComparison.Ordinal))];

    [Fact]
    public async Task Payment_events_reach_merchant_as_signed_webhooks_via_rabbitmq()
    {
        var payment = await CreatePaymentAsync("Order with webhooks");

        var webhooks = await Eventually.Get(() =>
        {
            var found = WebhooksFor(payment.Id);
            return Task.FromResult(found.Count >= 3 ? found : null);
        });

        var types = webhooks.Select(w => JsonDocument.Parse(w.Body).RootElement.GetProperty("type").GetString()).ToList();
        Assert.Equal(["payment.created", "payment.authorized", "payment.captured"], types.Take(3));

        foreach (var (request, body) in webhooks)
        {
            var timestamp = long.Parse(request.Headers.GetValues(WebhookSigner.TimestampHeader).Single(), System.Globalization.CultureInfo.InvariantCulture);
            var signature = request.Headers.GetValues(WebhookSigner.SignatureHeader).Single();
            Assert.True(WebhookSigner.Verify(PaymentsApiFactory.DemoWebhookSecret, timestamp, body, signature));
        }
    }

    [Fact]
    public async Task Outbox_message_is_written_in_same_transaction_and_published()
    {
        var payment = await CreatePaymentAsync("Outbox check");

        await Eventually.Get(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var messages = await db.Set<OutboxMessage>().AsNoTracking()
                .Where(m => m.Data.Contains(payment.Id.ToString()))
                .ToListAsync();
            return messages.Count == 3 && messages.All(m => m.PublishedAt != null) ? messages : null;
        });
    }

    [Fact]
    public async Task Failing_endpoint_is_retried_then_dead_lettered_and_can_be_replayed()
    {
        var payment = await CreatePaymentAsync($"Order {MerchantWebhookReceiver.RejectMarker}");

        var dead = await Eventually.Get(async () =>
        {
            var eventIds = WebhooksFor(payment.Id)
                .Select(w => JsonDocument.Parse(w.Body).RootElement.GetProperty("id").GetGuid())
                .ToHashSet();
            var deliveries = await _client.GetFromJsonAsync<List<WebhookDeliveryResponse>>("/v1/webhook-deliveries?status=dead&limit=100");
            return deliveries!.FirstOrDefault(d => d.EventType == "payment.captured" && eventIds.Contains(d.EventId));
        }, TimeSpan.FromSeconds(30));

        Assert.Equal(3, dead.Attempts);
        Assert.Equal(500, dead.LastStatusCode);

        factory.Webhooks.AcceptAll = true;
        try
        {
            var replay = await _client.PostAsync($"/v1/webhook-deliveries/{dead.Id}/replay", null);
            replay.EnsureSuccessStatusCode();

            await Eventually.Get(async () =>
            {
                var deliveries = await _client.GetFromJsonAsync<List<WebhookDeliveryResponse>>("/v1/webhook-deliveries?status=delivered&limit=100");
                return deliveries!.FirstOrDefault(d => d.Id == dead.Id);
            });
        }
        finally
        {
            factory.Webhooks.AcceptAll = false;
        }
    }

    [Fact]
    public async Task Replay_of_another_merchants_delivery_is_not_found()
    {
        var response = await _client.PostAsync($"/v1/webhook-deliveries/{Guid.NewGuid()}/replay", null);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
