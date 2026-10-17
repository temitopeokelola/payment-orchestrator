using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PaymentOrchestrator.Application;

/// <summary>Custom traces and business metrics. Exported through OpenTelemetry by the host.</summary>
public static class Telemetry
{
    public const string Name = "PaymentOrchestrator";

    public static readonly ActivitySource ActivitySource = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> PaymentsCreated =
        Meter.CreateCounter<long>("payments.created", "{payment}", "Payments created, by currency.");

    public static readonly Counter<long> PaymentOutcomes =
        Meter.CreateCounter<long>("payments.outcomes", "{payment}", "Status of payments when the create request finished, by provider.");

    public static readonly Histogram<double> ProviderCallDuration =
        Meter.CreateHistogram<double>("provider.call.duration", "s", "Duration of calls to payment providers.");

    public static readonly Counter<long> ProviderCalls =
        Meter.CreateCounter<long>("provider.calls", "{call}", "Calls to payment providers, by outcome.");

    public static readonly Counter<long> LedgerEntries =
        Meter.CreateCounter<long>("ledger.entries", "{entry}", "Journal entries posted, by type.");

    public static readonly Counter<long> ReconciliationDifferences =
        Meter.CreateCounter<long>("reconciliation.differences", "{item}", "Differences found by reconciliation, by provider and kind.");
}
