using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PaymentService>();
        return services;
    }
}
