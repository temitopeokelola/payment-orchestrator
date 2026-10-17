using System.Globalization;
using System.Text;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Reconciliation;
using PaymentOrchestrator.Application.Routing;

namespace PaymentOrchestrator.Application.Incidents;

/// <summary>Aggregated provider calls of one kind in the incident window.</summary>
public sealed record AttemptGroup(string Provider, string Operation, string Outcome, string? Code, int Count, double AverageLatencyMs);

public sealed record ProviderState(string Provider, string Circuit, int RecentCalls, double SuccessRate);

public sealed record UnresolvedDifference(string Provider, string Kind, Guid? PaymentId, string Detail);

/// <summary>Everything an operator needs to understand what is going wrong right now.</summary>
public sealed record IncidentSnapshot(
    DateTimeOffset Since,
    DateTimeOffset Until,
    IReadOnlyList<ProviderState> Providers,
    IReadOnlyList<AttemptGroup> Attempts,
    IReadOnlyList<UnresolvedDifference> Differences);

public sealed record IncidentSummary(string GeneratedBy, string Summary, IncidentSnapshot Snapshot);

public interface IIncidentSummarizer
{
    /// <summary>Returns a plain-English summary, or null if this summarizer is unavailable.</summary>
    Task<(string Summary, string Model)?> SummarizeAsync(IncidentSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IAttemptStatistics
{
    Task<IReadOnlyList<AttemptGroup>> GroupSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed class IncidentService(
    IProviderRegistry providers,
    IProviderHealth health,
    IAttemptStatistics attempts,
    IReconciliationStore reconciliation,
    IIncidentSummarizer summarizer,
    TimeProvider clock)
{
    public async Task<IncidentSummary> SummarizeAsync(int minutes, CancellationToken cancellationToken)
    {
        var until = clock.GetUtcNow();
        var since = until.AddMinutes(-Math.Clamp(minutes, 1, 24 * 60));

        var snapshot = new IncidentSnapshot(
            since,
            until,
            [.. providers.All.Select(p =>
            {
                var s = health.Snapshot(p.Name);
                return new ProviderState(p.Name, s.Circuit.ToString().ToLowerInvariant(), s.Samples, Math.Round(s.SuccessRate, 4));
            })],
            await attempts.GroupSinceAsync(since, cancellationToken),
            [.. (await reconciliation.ListAsync(null, 20, cancellationToken))
                .Where(r => r.CompletedAt >= since)
                .SelectMany(r => r.Items.Where(i => !i.Resolved).Select(i => new UnresolvedDifference(r.Provider, i.Kind.ToString(), i.PaymentId, i.Detail)))
                .Take(50)]);

        if (await summarizer.SummarizeAsync(snapshot, cancellationToken) is { } generated)
        {
            return new IncidentSummary(generated.Model, generated.Summary, snapshot);
        }

        return new IncidentSummary("rules", RuleBasedSummary(snapshot), snapshot);
    }

    /// <summary>Deterministic summary used when no language model is configured or it is unavailable.</summary>
    public static string RuleBasedSummary(IncidentSnapshot snapshot)
    {
        var text = new StringBuilder();
        var open = snapshot.Providers.Where(p => p.Circuit != "closed").ToList();
        var failures = snapshot.Attempts.Where(a => a.Outcome is "unavailable" or "unknown").ToList();
        var total = snapshot.Attempts.Sum(a => a.Count);

        if (open.Count == 0 && failures.Count == 0 && snapshot.Differences.Count == 0)
        {
            return $"No incidents: {total} provider calls since {snapshot.Since:HH:mm} UTC, all circuits closed, no unresolved reconciliation differences.";
        }

        foreach (var provider in open)
        {
            text.Append(CultureInfo.InvariantCulture, $"{provider.Provider} circuit is {provider.Circuit} (success ratio {provider.SuccessRate:P0} over {provider.RecentCalls} recent calls). ");
        }

        foreach (var group in failures.GroupBy(f => f.Provider).OrderByDescending(g => g.Sum(f => f.Count)))
        {
            var count = group.Sum(f => f.Count);
            var codes = string.Join(", ", group.Where(f => f.Code is not null).Select(f => f.Code).Distinct());
            text.Append(CultureInfo.InvariantCulture, $"{group.Key}: {count} failed calls ({codes}). ");
        }

        if (snapshot.Differences.Count > 0)
        {
            var kinds = string.Join(", ", snapshot.Differences.GroupBy(d => d.Kind).Select(g => $"{g.Count()} {g.Key}"));
            text.Append(CultureInfo.InvariantCulture, $"Unresolved reconciliation differences: {kinds}.");
        }

        return text.ToString().Trim();
    }
}

/// <summary>Used when no summarizer is configured.</summary>
public sealed class NoIncidentSummarizer : IIncidentSummarizer
{
    public Task<(string Summary, string Model)?> SummarizeAsync(IncidentSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.FromResult<(string, string)?>(null);
}
