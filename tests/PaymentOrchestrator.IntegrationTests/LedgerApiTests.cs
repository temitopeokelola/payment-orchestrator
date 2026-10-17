using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PaymentOrchestrator.Application.Ledger;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Infrastructure.Persistence;

namespace PaymentOrchestrator.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class LedgerApiTests(PaymentsApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateMerchantClient();

    private async Task<PaymentResponse> CreatePaymentAsync(long amount, bool capture = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(amount, "NGN", "Order", "sim_card_approved", "ada@example.com", capture)),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
    }

    private async Task<long> NairaBalanceAsync()
    {
        var balance = await _client.GetFromJsonAsync<MerchantBalanceResponse>("/v1/balance");
        return balance!.Available.SingleOrDefault(b => b.Currency == "NGN")?.Amount ?? 0;
    }

    [Fact]
    public async Task Capture_posts_balanced_entry_with_fees()
    {
        var payment = await CreatePaymentAsync(1_000_000);

        var entries = await _client.GetFromJsonAsync<List<JournalEntryResponse>>($"/v1/payments/{payment.Id}/ledger");

        var entry = Assert.Single(entries!);
        Assert.Equal("capture", entry.Type);
        Assert.Equal(0, entry.Postings.Sum(p => p.Amount));
        Assert.Contains(entry.Postings, p => p.Account == "merchant:merchant_demo:payable" && p.Amount == -980_000);
        Assert.Contains(entry.Postings, p => p.Account == "provider:simulator-a:clearing" && p.Amount == 986_000);
    }

    [Fact]
    public async Task Authorization_alone_does_not_touch_the_ledger()
    {
        var payment = await CreatePaymentAsync(5_000, capture: false);

        var entries = await _client.GetFromJsonAsync<List<JournalEntryResponse>>($"/v1/payments/{payment.Id}/ledger");

        Assert.Empty(entries!);
    }

    [Fact]
    public async Task Capture_and_refund_move_merchant_balance()
    {
        var before = await NairaBalanceAsync();
        var payment = await CreatePaymentAsync(100_000);
        var afterCapture = await NairaBalanceAsync();

        var refund = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{payment.Id}/refunds")
        {
            Content = JsonContent.Create(new RefundPaymentRequest(30_000)),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        (await _client.SendAsync(refund)).EnsureSuccessStatusCode();
        var afterRefund = await NairaBalanceAsync();

        Assert.Equal(98_000, afterCapture - before); // 100,000 minus 2% platform fee
        Assert.Equal(-30_000, afterRefund - afterCapture);
    }

    [Fact]
    public async Task Trial_balance_always_nets_to_zero()
    {
        await CreatePaymentAsync(12_345);

        var trial = await factory.CreateOperatorClient().GetFromJsonAsync<TrialBalanceResponse>("/v1/admin/ledger/trial-balance");

        Assert.True(trial!.Balanced);
        Assert.All(trial.TotalsByCurrency.Values, total => Assert.Equal(0, total));
    }

    [Fact]
    public async Task Trial_balance_requires_operator_key()
    {
        var response = await _client.GetAsync("/v1/admin/ledger/trial-balance");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_call_merchant_endpoints()
    {
        var response = await factory.CreateOperatorClient().GetAsync("/v1/balance");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Database_rejects_unbalanced_entry()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var entryId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO ledger_entries (id, payment_id, type, currency, created_at) VALUES ({entryId}, {Guid.NewGuid()}, 'Capture', 'NGN', now());
            INSERT INTO ledger_postings (entry_id, account, amount) VALUES ({entryId}, 'a', 100), ({entryId}, 'b', -99);
            """));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Database_rejects_editing_posted_entries()
    {
        await CreatePaymentAsync(1_000);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE ledger_postings SET amount = amount + 1"));

        Assert.Equal("55000", ex.SqlState);
    }
}
