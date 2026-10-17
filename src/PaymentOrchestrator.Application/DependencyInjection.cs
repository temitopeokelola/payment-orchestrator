using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Ledger;
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
        services.AddSingleton<IPaymentRouter, CapabilityRouter>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ProviderWebhookService>();
        services.AddScoped<LedgerQueries>();
        services.AddScoped<IDomainEventSubscriber<PaymentCaptured>, LedgerPostingHandler>();
        services.AddScoped<IDomainEventSubscriber<PaymentRefunded>, LedgerPostingHandler>();
        return services;
    }
}
