using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Ledger;
using PaymentOrchestrator.Application.Outbox;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<IProviderHealth, ProviderHealthTracker>();
        services.AddSingleton<IPaymentRouter, HealthAwareRouter>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ProviderWebhookService>();
        services.AddScoped<LedgerQueries>();
        services.AddScoped<IDomainEventSubscriber<PaymentCaptured>, LedgerPostingHandler>();
        services.AddScoped<IDomainEventSubscriber<PaymentRefunded>, LedgerPostingHandler>();
        services.AddScoped<MerchantWebhookFanout>();

        services.AddScoped<PaymentOutboxSubscriber>();
        services.AddScoped<IDomainEventSubscriber<PaymentCreated>>(sp => sp.GetRequiredService<PaymentOutboxSubscriber>());
        services.AddScoped<IDomainEventSubscriber<PaymentAuthorized>>(sp => sp.GetRequiredService<PaymentOutboxSubscriber>());
        services.AddScoped<IDomainEventSubscriber<PaymentCaptured>>(sp => sp.GetRequiredService<PaymentOutboxSubscriber>());
        services.AddScoped<IDomainEventSubscriber<PaymentRefunded>>(sp => sp.GetRequiredService<PaymentOutboxSubscriber>());
        services.AddScoped<IDomainEventSubscriber<PaymentFailed>>(sp => sp.GetRequiredService<PaymentOutboxSubscriber>());
        services.AddScoped<IDomainEventSubscriber<PaymentCancelled>>(sp => sp.GetRequiredService<PaymentOutboxSubscriber>());

        return services;
    }
}
