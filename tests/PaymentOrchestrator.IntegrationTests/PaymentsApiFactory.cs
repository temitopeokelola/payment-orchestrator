using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Infrastructure.Webhooks;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace PaymentOrchestrator.IntegrationTests;

public sealed class PaymentsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DemoApiKey = "sk_test_demo_merchant_key";
    public const string DemoWebhookSecret = "whsec_demo_merchant";
    public const string OperatorApiKey = "sk_ops_demo_operator_key";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    /// <summary>Stands in for the merchant's server, receiving outgoing webhooks.</summary>
    public MerchantWebhookReceiver Webhooks { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Payments", _postgres.GetConnectionString());
        builder.UseSetting("RabbitMq:ConnectionString", _rabbit.GetConnectionString());
        builder.UseSetting("Merchants:Credentials:0:WebhookUrl", "https://merchant.test/webhooks");
        builder.UseSetting("Workers:OutboxPollMilliseconds", "100");
        builder.UseSetting("Workers:WebhookPollMilliseconds", "100");
        builder.UseSetting("Workers:WebhookMaxAttempts", "3");
        builder.UseSetting("Workers:WebhookRetryBaseSeconds", "0");

        // Health-based demotion is covered by unit tests; keep routing deterministic across shared integration tests.
        builder.UseSetting("Routing:MinimumSamples", "100000");
        builder.UseSetting("Routing:CircuitMinimumThroughput", "6");
        builder.UseSetting("Routing:CircuitBreakSeconds", "1");

        // A dedicated pair of providers for circuit breaker tests, isolated by currency.
        builder.UseSetting("Providers:Simulators:2:Name", "chaos-primary");
        builder.UseSetting("Providers:Simulators:2:Currencies:0", "ZAR");
        builder.UseSetting("Providers:Simulators:2:FeeBasisPoints", "100");
        builder.UseSetting("Providers:Simulators:3:Name", "chaos-backup");
        builder.UseSetting("Providers:Simulators:3:Currencies:0", "ZAR");
        builder.UseSetting("Providers:Simulators:3:FeeBasisPoints", "300");

        builder.ConfigureTestServices(services =>
            services.AddHttpClient(WebhookSigner.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Webhooks));
    }

    public HttpClient CreateMerchantClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DemoApiKey);
        return client;
    }

    public HttpClient CreateOperatorClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OperatorApiKey);
        return client;
    }

    public async Task InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

/// <summary>Records every webhook. Payments whose description contains "reject-webhooks" get a 500 back.</summary>
public sealed class MerchantWebhookReceiver : HttpMessageHandler
{
    public const string RejectMarker = "reject-webhooks";

    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Received { get; } = new();

    public bool AcceptAll { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Received.Enqueue((request, body));

        return !AcceptAll && body.Contains(RejectMarker, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<PaymentsApiFactory>
{
    public const string Name = "api";
}
