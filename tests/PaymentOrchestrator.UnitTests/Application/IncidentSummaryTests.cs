using PaymentOrchestrator.Application.Incidents;

namespace PaymentOrchestrator.UnitTests.Application;

public class IncidentSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);

    private static IncidentSnapshot Snapshot(
        IReadOnlyList<ProviderState>? providers = null,
        IReadOnlyList<AttemptGroup>? attempts = null,
        IReadOnlyList<UnresolvedDifference>? differences = null) =>
        new(Now.AddMinutes(-15), Now, providers ?? [], attempts ?? [], differences ?? []);

    [Fact]
    public void Quiet_period_reports_no_incidents()
    {
        var summary = IncidentService.RuleBasedSummary(Snapshot(
            providers: [new ProviderState("simulator-a", "closed", 120, 1)],
            attempts: [new AttemptGroup("simulator-a", "authorize", "approved", null, 120, 45)]));

        Assert.StartsWith("No incidents", summary, StringComparison.Ordinal);
        Assert.Contains("120 provider calls", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_circuit_and_failures_are_named()
    {
        var summary = IncidentService.RuleBasedSummary(Snapshot(
            providers: [new ProviderState("simulator-a", "open", 40, 0.25)],
            attempts:
            [
                new AttemptGroup("simulator-a", "authorize", "unavailable", "provider_outage", 30, 41),
                new AttemptGroup("simulator-b", "authorize", "approved", null, 70, 62),
            ]));

        Assert.Contains("simulator-a circuit is open", summary, StringComparison.Ordinal);
        Assert.Contains("30 failed calls (provider_outage)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("simulator-b:", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Money_at_risk_differences_are_listed()
    {
        var summary = IncidentService.RuleBasedSummary(Snapshot(
            differences:
            [
                new UnresolvedDifference("simulator-a", "ChargedButFailed", Guid.NewGuid(), "Refund required."),
                new UnresolvedDifference("simulator-a", "ChargedButFailed", Guid.NewGuid(), "Refund required."),
            ]));

        Assert.Contains("2 ChargedButFailed", summary, StringComparison.Ordinal);
    }
}
