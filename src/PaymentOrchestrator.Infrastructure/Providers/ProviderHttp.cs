using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaymentOrchestrator.Application.Providers;

namespace PaymentOrchestrator.Infrastructure.Providers;

internal static class ProviderHttp
{
    /// <summary>
    /// Sends a request and turns transport failures into provider outcomes. A connection that was never
    /// established is safe to retry elsewhere; anything after the request may have left is unknown.
    /// </summary>
    public static async Task<(HttpResponseMessage? Response, ProviderResult? Failure)> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await client.SendAsync(request, cancellationToken), null);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            return (null, ProviderResult.Unavailable("connection_failed", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return (null, ProviderResult.Unknown("transport_error", ex.Message));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, ProviderResult.Unknown("timeout", "The provider did not respond in time."));
        }
    }

    /// <summary>Rate limits are refused before processing; other server errors may have been processed.</summary>
    public static ProviderResult FromServerError(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests
        ? ProviderResult.Unavailable("rate_limited", "The provider is rate limiting requests.")
        : ProviderResult.Unknown($"http_{(int)status}", "The provider returned a server error.");

    public static bool FixedTimeEqualsHex(string expectedHex, string actualHex) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(actualHex.Trim().ToLowerInvariant()));
}
