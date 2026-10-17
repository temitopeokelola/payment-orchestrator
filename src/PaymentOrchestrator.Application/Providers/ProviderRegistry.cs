namespace PaymentOrchestrator.Application.Providers;

public interface IProviderRegistry
{
    IReadOnlyList<IPaymentProvider> All { get; }

    IPaymentProvider? Find(string name);
}

public sealed class ProviderRegistry(IEnumerable<IPaymentProvider> providers) : IProviderRegistry
{
    private readonly Dictionary<string, IPaymentProvider> _byName =
        providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IPaymentProvider> All => [.. _byName.Values];

    public IPaymentProvider? Find(string name) => _byName.GetValueOrDefault(name);
}
