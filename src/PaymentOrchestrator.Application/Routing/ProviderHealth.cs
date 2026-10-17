using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace PaymentOrchestrator.Application.Routing;

public sealed class RoutingOptions
{
    public const string SectionName = "Routing";

    /// <summary>How far back provider outcomes count towards health.</summary>
    public int HealthWindowSeconds { get; set; } = 60;

    /// <summary>Health is ignored until a provider has at least this many recent calls.</summary>
    public int MinimumSamples { get; set; } = 5;

    /// <summary>Cost added per 100% failure rate, in basis points. 2000 means a 10% failure rate costs as much as a 2% fee.</summary>
    public int FailurePenaltyBasisPoints { get; set; } = 2_000;

    /// <summary>Circuit breaker: open when at least this share of calls fail...</summary>
    public double CircuitFailureRatio { get; set; } = 0.5;

    /// <summary>...over at least this many calls within the sampling window...</summary>
    public int CircuitMinimumThroughput { get; set; } = 10;

    public int CircuitSamplingSeconds { get; set; } = 30;

    /// <summary>...and stay open this long before letting a trial call through.</summary>
    public int CircuitBreakSeconds { get; set; } = 15;

    /// <summary>Per-provider breaker tuning, keyed by provider name. High-volume providers can use stricter settings.</summary>
    public Dictionary<string, CircuitOverride> CircuitOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public (double FailureRatio, int MinimumThroughput, int BreakSeconds) CircuitFor(string provider) =>
        CircuitOverrides.TryGetValue(provider, out var o)
            ? (o.FailureRatio ?? CircuitFailureRatio, o.MinimumThroughput ?? CircuitMinimumThroughput, o.BreakSeconds ?? CircuitBreakSeconds)
            : (CircuitFailureRatio, CircuitMinimumThroughput, CircuitBreakSeconds);
}

public sealed class CircuitOverride
{
    public double? FailureRatio { get; set; }
    public int? MinimumThroughput { get; set; }
    public int? BreakSeconds { get; set; }
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public sealed record ProviderHealthSnapshot(string Provider, int Samples, double SuccessRate, double AverageLatencyMs, CircuitState Circuit);

public interface IProviderHealth
{
    /// <param name="success">The provider answered (approved or declined). Outages and unknown outcomes are failures.</param>
    void Record(string provider, bool success, TimeSpan latency);

    /// <param name="retryAt">For an open circuit, when the breaker will allow a trial call.</param>
    void SetCircuit(string provider, CircuitState state, DateTimeOffset? retryAt = null);

    ProviderHealthSnapshot Snapshot(string provider);
}

/// <summary>Sliding time window of call outcomes per provider.</summary>
public sealed class ProviderHealthTracker(TimeProvider clock, IOptions<RoutingOptions> options) : IProviderHealth
{
    private readonly ConcurrentDictionary<string, Stats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string provider, bool success, TimeSpan latency)
    {
        var stats = _stats.GetOrAdd(provider, _ => new Stats());
        lock (stats)
        {
            stats.Samples.Enqueue((clock.GetUtcNow(), success, latency.TotalMilliseconds));
            Prune(stats);
        }
    }

    public void SetCircuit(string provider, CircuitState state, DateTimeOffset? retryAt = null)
    {
        var stats = _stats.GetOrAdd(provider, _ => new Stats());
        lock (stats)
        {
            stats.Circuit = state;
            stats.RetryAt = retryAt;
        }
    }

    public ProviderHealthSnapshot Snapshot(string provider)
    {
        var stats = _stats.GetOrAdd(provider, _ => new Stats());
        lock (stats)
        {
            Prune(stats);

            // Once the break has elapsed the breaker lets a trial call through, so report it as half-open;
            // otherwise a router that skips open circuits would never send the call that closes it.
            var circuit = stats.Circuit == CircuitState.Open && stats.RetryAt <= clock.GetUtcNow()
                ? CircuitState.HalfOpen
                : stats.Circuit;

            var count = stats.Samples.Count;
            return count == 0
                ? new ProviderHealthSnapshot(provider, 0, 1, 0, circuit)
                : new ProviderHealthSnapshot(
                    provider,
                    count,
                    stats.Samples.Count(s => s.Success) / (double)count,
                    stats.Samples.Average(s => s.LatencyMs),
                    circuit);
        }
    }

    private void Prune(Stats stats)
    {
        var cutoff = clock.GetUtcNow().AddSeconds(-options.Value.HealthWindowSeconds);
        while (stats.Samples.TryPeek(out var oldest) && oldest.At < cutoff)
        {
            stats.Samples.Dequeue();
        }
    }

    private sealed class Stats
    {
        public Queue<(DateTimeOffset At, bool Success, double LatencyMs)> Samples { get; } = new();

        public CircuitState Circuit { get; set; }

        public DateTimeOffset? RetryAt { get; set; }
    }
}
