namespace PaymentOrchestrator.Infrastructure.Providers.Simulator;

public sealed class SimulatorOptions
{
    public string Name { get; set; } = "simulator";

    public List<string> Currencies { get; set; } = [];

    public int FeeBasisPoints { get; set; } = 150;

    /// <summary>Base latency added to every call, to make load tests and traces realistic.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Probability (0-1) that a call is refused as if the provider were down.</summary>
    public double FailureRate { get; set; }

    public string WebhookSecret { get; set; } = "whsec_simulator";
}
