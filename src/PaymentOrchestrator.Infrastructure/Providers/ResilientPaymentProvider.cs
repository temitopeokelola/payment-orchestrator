using System.Diagnostics;
using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Routing;
using PaymentOrchestrator.Domain.Payments;
using Polly;
using Polly.CircuitBreaker;
using CircuitState = PaymentOrchestrator.Application.Routing.CircuitState;

namespace PaymentOrchestrator.Infrastructure.Providers;

/// <summary>
/// Wraps a provider with a circuit breaker and health tracking. When the circuit is open, calls fail fast as
/// <see cref="ProviderOutcome.Unavailable"/> without touching the provider, which is always safe to fail over from.
/// </summary>
internal sealed class ResilientPaymentProvider : IPaymentProvider
{
    private readonly IPaymentProvider _inner;
    private readonly IProviderHealth _health;
    private readonly ResiliencePipeline<ProviderResult> _pipeline;

    public ResilientPaymentProvider(IPaymentProvider inner, IProviderHealth health, RoutingOptions options, TimeProvider clock)
    {
        _inner = inner;
        _health = health;

        _pipeline = new ResiliencePipelineBuilder<ProviderResult> { TimeProvider = clock }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ProviderResult>
            {
                ShouldHandle = new PredicateBuilder<ProviderResult>()
                    .HandleResult(r => r.Outcome is ProviderOutcome.Unavailable or ProviderOutcome.Unknown),
                FailureRatio = options.CircuitFailureRatio,
                MinimumThroughput = options.CircuitMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakSeconds),
                OnOpened = args => SetCircuit(CircuitState.Open, clock.GetUtcNow() + args.BreakDuration),
                OnHalfOpened = _ => SetCircuit(CircuitState.HalfOpen),
                OnClosed = _ => SetCircuit(CircuitState.Closed),
            })
            .Build();
    }

    public string Name => _inner.Name;

    public ProviderCapabilities Capabilities => _inner.Capabilities;

    public Task<ProviderResult> AuthorizeAsync(AuthorizeCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(ct => _inner.AuthorizeAsync(command, ct), cancellationToken);

    public Task<ProviderResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) =>
        ExecuteAsync(ct => _inner.CaptureAsync(paymentId, providerReference, amount, ct), cancellationToken);

    public Task<ProviderResult> RefundAsync(Guid paymentId, string providerReference, Money amount, string refundKey, CancellationToken cancellationToken) =>
        ExecuteAsync(ct => _inner.RefundAsync(paymentId, providerReference, amount, refundKey, ct), cancellationToken);

    public Task<ProviderResult> CancelAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken) =>
        ExecuteAsync(ct => _inner.CancelAsync(paymentId, providerReference, ct), cancellationToken);

    public ProviderWebhookEvent ParseWebhook(IReadOnlyDictionary<string, string> headers, string body) =>
        _inner.ParseWebhook(headers, body);

    private async Task<ProviderResult> ExecuteAsync(Func<CancellationToken, Task<ProviderResult>> call, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _pipeline.ExecuteAsync(async ct => await call(ct), cancellationToken);
            _health.Record(Name, result.Outcome is ProviderOutcome.Approved or ProviderOutcome.Declined, Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (BrokenCircuitException)
        {
            return ProviderResult.Unavailable("circuit_open", $"{Name} is temporarily disabled after repeated failures.");
        }
    }

    private ValueTask SetCircuit(CircuitState state, DateTimeOffset? retryAt = null)
    {
        _health.SetCircuit(Name, state, retryAt);
        return ValueTask.CompletedTask;
    }
}
