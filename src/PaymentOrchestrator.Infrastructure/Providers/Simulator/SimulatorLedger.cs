using System.Collections.Concurrent;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Infrastructure.Providers.Simulator;

public enum SimulatedStatus
{
    Authorized,
    Captured,
    Cancelled,
}

public sealed class SimulatedTransaction
{
    public required string Provider { get; init; }
    public required string Reference { get; init; }
    public required Guid PaymentId { get; init; }
    public required Money Amount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public SimulatedStatus Status { get; set; }
    public long RefundedMinor { get; set; }
    public HashSet<string> RefundKeys { get; } = [];
}

/// <summary>
/// In-memory record of everything the simulated providers have processed. Acts as the provider's own
/// books, which reconciliation compares against ours.
/// </summary>
public sealed class SimulatorLedger
{
    private readonly ConcurrentDictionary<string, SimulatedTransaction> _byReference = new();
    private readonly ConcurrentDictionary<(string Provider, Guid PaymentId), string> _byPayment = new();
    private readonly ConcurrentDictionary<string, bool> _outages = new(StringComparer.OrdinalIgnoreCase);

    public SimulatedTransaction GetOrAdd(string provider, Guid paymentId, Func<SimulatedTransaction> create)
    {
        var reference = _byPayment.GetOrAdd((provider, paymentId), _ =>
        {
            var transaction = create();
            _byReference[transaction.Reference] = transaction;
            return transaction.Reference;
        });

        return _byReference[reference];
    }

    public SimulatedTransaction? Find(string reference) => _byReference.GetValueOrDefault(reference);

    public IReadOnlyList<SimulatedTransaction> Transactions(string provider) =>
        [.. _byReference.Values.Where(t => t.Provider == provider).OrderBy(t => t.CreatedAt)];

    public void SetOutage(string provider, bool down) => _outages[provider] = down;

    public bool IsDown(string provider) => _outages.GetValueOrDefault(provider);
}
