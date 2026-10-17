using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentOrchestrator.Application.Abstractions;
using PaymentOrchestrator.Application.Idempotency;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Infrastructure.Idempotency;
using PaymentOrchestrator.Infrastructure.Persistence;
using PaymentOrchestrator.Infrastructure.Providers.Simulator;

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
    }
}
