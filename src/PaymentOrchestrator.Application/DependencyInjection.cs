using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Application.Webhooks;

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
        return services;
    }
}
