using System.Net.Http.Headers;
using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Idempotency;
using PaymentOrchestrator.Application.Incidents;
using PaymentOrchestrator.Application.Ledger;
using PaymentOrchestrator.Application.Outbox;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Reconciliation;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Idempotency;
using PaymentOrchestrator.Infrastructure.Incidents;
using PaymentOrchestrator.Infrastructure.Messaging;
using PaymentOrchestrator.Infrastructure.Outbox;
using PaymentOrchestrator.Infrastructure.Persistence;
using PaymentOrchestrator.Infrastructure.Providers;
using PaymentOrchestrator.Infrastructure.Providers.Paystack;
using PaymentOrchestrator.Infrastructure.Providers.Simulator;
using PaymentOrchestrator.Infrastructure.Providers.Stripe;
using PaymentOrchestrator.Infrastructure.Reconciliation;
using PaymentOrchestrator.Infrastructure.Webhooks;

namespace PaymentOrchestrator.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Payments")
            ?? throw new InvalidOperationException("Connection string 'Payments' is not configured.");

        services.AddDbContext<PaymentsDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<PaymentsDbContext>());
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddScoped<EfPaymentAttemptLog>();
        services.AddScoped<IPaymentAttemptLog>(sp => sp.GetRequiredService<EfPaymentAttemptLog>());
        services.AddScoped<IAttemptStatistics>(sp => sp.GetRequiredService<EfPaymentAttemptLog>());

        var incidents = configuration.GetSection(IncidentSummaryOptions.SectionName).Get<IncidentSummaryOptions>() ?? new IncidentSummaryOptions();
        if (string.IsNullOrWhiteSpace(incidents.AnthropicApiKey))
        {
            services.AddSingleton<IIncidentSummarizer, NoIncidentSummarizer>();
        }
        else
        {
            services.AddSingleton(new AnthropicClient { ApiKey = incidents.AnthropicApiKey });
            services.AddSingleton<IIncidentSummarizer>(sp => new ClaudeIncidentSummarizer(
                sp.GetRequiredService<AnthropicClient>(), incidents, sp.GetRequiredService<ILogger<ClaudeIncidentSummarizer>>()));
        }
        services.AddScoped<IReconciliationStore, EfReconciliationStore>();
        services.Configure<ReconciliationOptions>(configuration.GetSection(ReconciliationOptions.SectionName));
        services.AddHostedService<ReconciliationWorker>();
        services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddScoped<IProviderWebhookInbox, PostgresWebhookInbox>();

        services.Configure<LedgerOptions>(configuration.GetSection(LedgerOptions.SectionName));

        services.Configure<RoutingOptions>(configuration.GetSection(RoutingOptions.SectionName));
        services.AddPaymentProviders(configuration.GetSection("Providers"));

        // Every provider the application sees goes through a circuit breaker.
        services.Replace(ServiceDescriptor.Singleton<IProviderRegistry>(sp => new ProviderRegistry(
            sp.GetServices<IPaymentProvider>().Select(p => new ResilientPaymentProvider(
                p,
                sp.GetRequiredService<IProviderHealth>(),
                sp.GetRequiredService<IOptions<RoutingOptions>>().Value,
                sp.GetRequiredService<TimeProvider>())))));
        services.AddMessaging(configuration);

        return services;
    }

    private static void AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        services.Configure<MerchantWebhookOptions>(configuration.GetSection("Merchants"));

        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<IWebhookDeliveryStore, PostgresWebhookDeliveryStore>();
        services.AddSingleton<IMerchantWebhookDirectory, ConfigurationMerchantWebhookDirectory>();
        services.AddHttpClient(WebhookDeliveryWorker.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

        var rabbit = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        if (string.IsNullOrWhiteSpace(rabbit.ConnectionString))
        {
            services.AddSingleton<IEventPublisher, InMemoryEventPublisher>();
        }
        else
        {
            services.AddSingleton(new RabbitMqConnection(rabbit));
            services.AddSingleton<IEventPublisher, RabbitMqPublisher>();
            services.AddHostedService<RabbitMqWebhookConsumer>();
        }

        services.AddHostedService<OutboxProcessor>();
        services.AddHostedService<WebhookDeliveryWorker>();
    }

    private static void AddPaymentProviders(this IServiceCollection services, IConfiguration providers)
    {
        services.AddSingleton<SimulatorLedger>();

        foreach (var simulator in providers.GetSection("Simulators").Get<List<SimulatorOptions>>() ?? [])
        {
            services.AddSingleton(sp => new SimulatorProvider(
                simulator, sp.GetRequiredService<SimulatorLedger>(), sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IPaymentProvider>(sp => sp.GetServices<SimulatorProvider>().Single(p => p.Name == simulator.Name));
            services.AddSingleton<ISettlementReportSource>(sp => new SimulatorSettlementSource(simulator.Name, sp.GetRequiredService<SimulatorLedger>()));
        }

        var stripe = providers.GetSection("Stripe").Get<StripeOptions>();
        if (!string.IsNullOrWhiteSpace(stripe?.SecretKey))
        {
            services.AddHttpClient(StripeProvider.ProviderName, client =>
            {
                client.BaseAddress = new Uri("https://api.stripe.com/");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stripe.SecretKey);
            });
            services.AddSingleton<IPaymentProvider>(sp => new StripeProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(StripeProvider.ProviderName),
                stripe,
                sp.GetRequiredService<TimeProvider>()));
        }

        var paystack = providers.GetSection("Paystack").Get<PaystackOptions>();
        if (!string.IsNullOrWhiteSpace(paystack?.SecretKey))
        {
            services.AddHttpClient(PaystackProvider.ProviderName, client =>
            {
                client.BaseAddress = new Uri("https://api.paystack.co/");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paystack.SecretKey);
            });
            services.AddSingleton<IPaymentProvider>(sp => new PaystackProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(PaystackProvider.ProviderName),
                paystack));
        }
    }
}
