using Microsoft.Extensions.Options;
using PaymentOrchestrator.Application.Webhooks;

namespace PaymentOrchestrator.Infrastructure.Webhooks;

/// <summary>Reads merchant webhook endpoints from the same "Merchants" section that holds their API keys.</summary>
public sealed class MerchantWebhookOptions
{
    public List<MerchantWebhookSettings> Credentials { get; set; } = [];
}

public sealed class MerchantWebhookSettings
{
    public string MerchantId { get; set; } = string.Empty;
    public string? WebhookUrl { get; set; }
    public string? WebhookSecret { get; set; }
}

internal sealed class ConfigurationMerchantWebhookDirectory(IOptionsMonitor<MerchantWebhookOptions> options) : IMerchantWebhookDirectory
{
    public MerchantWebhookEndpoint? Find(string merchantId)
    {
        var settings = options.CurrentValue.Credentials.FirstOrDefault(c => c.MerchantId == merchantId);
        return settings is { WebhookUrl: { } url, WebhookSecret: { } secret } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new MerchantWebhookEndpoint(merchantId, uri, secret)
            : null;
    }
}
