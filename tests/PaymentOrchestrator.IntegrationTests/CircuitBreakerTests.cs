using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Infrastructure.Providers.Simulator;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class CircuitBreakerTests(PaymentsApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateMerchantClient();

    private sealed record ProviderStatus(string Name, string Circuit);

    private async Task<PaymentResponse> PayInRandAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(10_000, "ZAR", "Order", "sim_card_approved")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
    }

    private async Task<string> CircuitOfAsync(string provider) =>
        (await _client.GetFromJsonAsync<List<ProviderStatus>>("/v1/providers"))!.Single(p => p.Name == provider).Circuit;

    [Fact]
    public async Task Outage_opens_circuit_traffic_fails_over_and_circuit_recovers()
    {
        var ledger = factory.Services.GetRequiredService<SimulatorLedger>();
        ledger.SetOutage("chaos-primary", true);

        // Every payment still succeeds through the backup while the primary is down.
        for (var i = 0; i < 6; i++)
        {
            var payment = await PayInRandAsync();
            Assert.Equal("captured", payment.Status);
            Assert.Equal("chaos-backup", payment.Provider);
        }

        Assert.Equal("open", await CircuitOfAsync("chaos-primary"));

        // With the circuit open the primary is skipped without being called at all.
        var routedAround = await PayInRandAsync();
        var attempts = await _client.GetFromJsonAsync<List<PaymentAttempt>>($"/v1/payments/{routedAround.Id}/attempts");
        Assert.Equal("chaos-backup", Assert.Single(attempts!).Provider);

        // Provider recovers; after the break duration a trial call closes the circuit again.
        ledger.SetOutage("chaos-primary", false);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var recovered = await Eventually.Get(async () =>
        {
            var payment = await PayInRandAsync();
            return payment.Provider == "chaos-primary" ? payment : null;
        });

        Assert.Equal("captured", recovered.Status);
        Assert.Equal("closed", await CircuitOfAsync("chaos-primary"));
    }

    [Fact]
    public async Task Attempts_show_failover_path_with_latency()
    {
        var ledger = factory.Services.GetRequiredService<SimulatorLedger>();
        ledger.SetOutage("simulator-a", true);
        PaymentResponse payment;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
            {
                Content = JsonContent.Create(new CreatePaymentRequest(5_000, "NGN", "Order", "sim_card_approved", Capture: false)),
                Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
            };
            payment = (await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<PaymentResponse>())!;
        }
        finally
        {
            ledger.SetOutage("simulator-a", false);
        }

        var attempts = await _client.GetFromJsonAsync<List<PaymentAttempt>>($"/v1/payments/{payment.Id}/attempts");

        Assert.Collection(
            attempts!,
            a => Assert.Equal(("simulator-a", "authorize", "unavailable"), (a.Provider, a.Operation, a.Outcome)),
            a => Assert.Equal(("simulator-b", "authorize", "approved"), (a.Provider, a.Operation, a.Outcome)));
        Assert.All(attempts!, a => Assert.True(a.LatencyMs >= 0));
    }
}
