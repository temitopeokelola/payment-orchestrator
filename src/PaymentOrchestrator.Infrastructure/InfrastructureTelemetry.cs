using System.Diagnostics.Metrics;
using PaymentOrchestrator.Application;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;

namespace PaymentOrchestrator.Infrastructure;

public static class InfrastructureTelemetry
{
    internal static readonly Counter<long> OutboxPublished =
        Telemetry.Meter.CreateCounter<long>("outbox.published", "{message}", "Outbox messages published to the broker.");

    internal static readonly Histogram<double> OutboxLag =
        Telemetry.Meter.CreateHistogram<double>("outbox.lag", "s", "Time from an event being recorded to being published.");

    internal static readonly Counter<long> WebhookAttempts =
        Telemetry.Meter.CreateCounter<long>("webhooks.attempts", "{attempt}", "Merchant webhook delivery attempts, by result.");

    /// <summary>Publishes provider health as gauges, read on every scrape.</summary>
    public static void RegisterProviderGauges(IProviderRegistry registry, IProviderHealth health)
    {
        Telemetry.Meter.CreateObservableGauge(
            "provider.success_ratio",
            () => registry.All.Select(p => new Measurement<double>(
                health.Snapshot(p.Name).SuccessRate,
                new KeyValuePair<string, object?>("provider", p.Name))),
            unit: "1",
            description: "Share of recent provider calls that got an answer (approved or declined).");

        Telemetry.Meter.CreateObservableGauge(
            "provider.circuit_state",
            () => registry.All.Select(p => new Measurement<int>(
                (int)health.Snapshot(p.Name).Circuit,
                new KeyValuePair<string, object?>("provider", p.Name))),
            unit: "{state}",
            description: "Circuit breaker state per provider: 0 closed, 1 open, 2 half-open.");
    }
}
