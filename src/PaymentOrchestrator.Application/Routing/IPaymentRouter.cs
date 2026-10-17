using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Application.Routing;

public sealed record RoutingRequest(Money Amount, bool RequiresSeparateCapture, string? PreferredProvider);

public interface IPaymentRouter
{
    /// <summary>Returns eligible providers, best first. An empty list means no provider can take the payment.</summary>
    IReadOnlyList<IPaymentProvider> Rank(RoutingRequest request);
}

/// <summary>Filters providers by currency and capability, then orders by fee.</summary>
public sealed class CapabilityRouter(IProviderRegistry registry) : IPaymentRouter
{
    public IReadOnlyList<IPaymentProvider> Rank(RoutingRequest request)
    {
        var eligible = registry.All
            .Where(p => p.Capabilities.Currencies.Contains(request.Amount.Currency))
            .Where(p => !request.RequiresSeparateCapture || p.Capabilities.SupportsSeparateCapture)
            .OrderBy(p => p.Capabilities.FeeBasisPoints)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        if (request.PreferredProvider is { } preferred)
        {
            var match = eligible.FindIndex(p => string.Equals(p.Name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match > 0)
            {
                var provider = eligible[match];
                eligible.RemoveAt(match);
                eligible.Insert(0, provider);
            }
        }

        return eligible;
    }
}
