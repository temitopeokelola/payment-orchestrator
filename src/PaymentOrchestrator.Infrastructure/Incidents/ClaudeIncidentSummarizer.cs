using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using PaymentOrchestrator.Application.Incidents;

namespace PaymentOrchestrator.Infrastructure.Incidents;

public sealed class IncidentSummaryOptions
{
    public const string SectionName = "IncidentSummaries";

    /// <summary>Anthropic API key. When empty, the rule-based summary is used.</summary>
    public string? AnthropicApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";
}

/// <summary>
/// Asks Claude to turn the operational snapshot into a short summary for the on-call engineer.
/// Any failure (network, rate limit, refusal) returns null so the caller falls back to the rule-based summary.
/// </summary>
internal sealed partial class ClaudeIncidentSummarizer(
    AnthropicClient client,
    IncidentSummaryOptions options,
    ILogger<ClaudeIncidentSummarizer> logger) : IIncidentSummarizer
{
    private const string SystemPrompt = """
        You are the on-call assistant for a payment orchestration service that routes card payments across several
        providers with circuit breakers, a double-entry ledger and daily reconciliation.

        You receive a JSON snapshot covering a time window: provider circuit states and recent success ratios,
        provider calls grouped by provider, operation, outcome and error code, and unresolved reconciliation
        differences.

        Write a summary of at most 120 words for the engineer who just got paged:
        1. One sentence on overall impact: are customers' payments failing, or is traffic being routed around a problem?
        2. The likely cause, naming the provider and error codes. Only state what the data supports.
        3. Money at risk: call out any "ChargedButFailed" or "MissingAtProvider" differences, since those need action.
        4. Up to three concrete next steps.

        If nothing is wrong, say so in one sentence. Plain text, no headings, no markdown.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(string Summary, string Model)?> SummarizeAsync(IncidentSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Beta.Messages.Create(
                new MessageCreateParams
                {
                    Model = options.Model,
                    MaxTokens = 4000,
                    System = SystemPrompt,
                    OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                    // Route around a safety refusal to whichever model the API picks for that category.
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new Default(),
                    Messages = [new() { Role = Role.User, Content = JsonSerializer.Serialize(snapshot, Json) }],
                },
                cancellationToken);

            if (response.StopReason == "refusal")
            {
                LogRefused();
                return null;
            }

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)).Trim();
            return text.Length == 0 ? null : (text, response.Model.ToString());
        }
        catch (AnthropicApiException ex)
        {
            LogApiFailed(ex);
            return null;
        }
        catch (HttpRequestException ex)
        {
            LogApiFailed(ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident summary request was refused; using rule-based summary")]
    private partial void LogRefused();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident summary request failed; using rule-based summary")]
    private partial void LogApiFailed(Exception exception);
}
