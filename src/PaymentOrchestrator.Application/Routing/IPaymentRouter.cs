using Microsoft.Extensions.Options;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Routing;

public sealed record RoutingRequest(Money Amount, bool RequiresSeparateCapture, string? PreferredProvider);

public interface IPaymentRouter
{
    /// <summary>Returns eligible providers, best first. An empty list means no provider can take the payment.</summary>
    IReadOnlyList<IPaymentProvider> Rank(RoutingRequest request);
}

/// <summary>
/// Ranks providers that support the currency and capture mode by effective cost: their fee plus a penalty for
/// recent failures. Providers with an open circuit go last; they are kept so the breaker can probe recovery.
/// </summary>
public sealed class HealthAwareRouter(IProviderRegistry registry, IProviderHealth health, IOptions<RoutingOptions> options) : IPaymentRouter
{
    public IReadOnlyList<IPaymentProvider> Rank(RoutingRequest request)
    {
        var settings = options.Value;

        var ranked = registry.All
            .Where(p => p.Capabilities.Currencies.Contains(request.Amount.Currency))
            .Where(p => !request.RequiresSeparateCapture || p.Capabilities.SupportsSeparateCapture)
            .Select(p => (Provider: p, Health: health.Snapshot(p.Name)))
            .OrderBy(x => x.Health.Circuit == CircuitState.Open)
            .ThenBy(x => string.Equals(x.Provider.Name, request.PreferredProvider, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => EffectiveCost(x.Provider, x.Health, settings))
            .ThenBy(x => x.Provider.Name, StringComparer.Ordinal)
            .Select(x => x.Provider)
            .ToList();

        return ranked;
    }

    internal static double EffectiveCost(IPaymentProvider provider, ProviderHealthSnapshot health, RoutingOptions settings)
    {
        var penalty = health.Samples >= settings.MinimumSamples
            ? (1 - health.SuccessRate) * settings.FailurePenaltyBasisPoints
            : 0;

        return provider.Capabilities.FeeBasisPoints + penalty;
    }
}
