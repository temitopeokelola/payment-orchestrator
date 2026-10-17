using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Idempotency;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Idempotency;
using PaymentOrchestrator.Infrastructure.Persistence;
using PaymentOrchestrator.Infrastructure.Providers.Paystack;
using PaymentOrchestrator.Infrastructure.Providers.Simulator;
using PaymentOrchestrator.Infrastructure.Providers.Stripe;
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

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<PaymentsDbContext>());
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddScoped<IProviderWebhookInbox, PostgresWebhookInbox>();

        services.AddPaymentProviders(configuration.GetSection("Providers"));

        return services;
    }

    private static void AddPaymentProviders(this IServiceCollection services, IConfiguration providers)
    {
        services.AddSingleton<SimulatorLedger>();

        foreach (var simulator in providers.GetSection("Simulators").Get<List<SimulatorOptions>>() ?? [])
        {
            services.AddSingleton(sp => new SimulatorProvider(
                simulator, sp.GetRequiredService<SimulatorLedger>(), sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IPaymentProvider>(sp => sp.GetServices<SimulatorProvider>().Single(p => p.Name == simulator.Name));
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
