using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PaymentOrchestrator.Api.Authentication;

public sealed class MerchantOptions
{
    public const string SectionName = "Merchants";

    public List<MerchantCredential> Credentials { get; init; } = [];
}

public sealed class MerchantCredential
{
    public required string MerchantId { get; init; }

    /// <summary>Hex encoded SHA-256 of the secret key. Raw keys are never stored.</summary>
    public required string ApiKeySha256 { get; init; }
}

public sealed class OperatorOptions
{
    public const string SectionName = "Operators";

    public List<OperatorCredential> Credentials { get; init; } = [];
}

public sealed class OperatorCredential
{
    public required string Name { get; init; }

    /// <summary>Hex encoded SHA-256 of the operator key.</summary>
    public required string ApiKeySha256 { get; init; }
}

public static class AuthorizationPolicies
{
    public const string Merchant = "merchant";
    public const string Operator = "operator";
}

/// <summary>
/// Authenticates merchants with "Authorization: Bearer sk_..." secret keys.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<MerchantOptions> merchants,
    IOptionsMonitor<OperatorOptions> operators)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string MerchantIdClaim = "merchant_id";
    public const string OperatorRole = "operator";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var key = header["Bearer ".Length..].Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        foreach (var credential in merchants.CurrentValue.Credentials)
        {
            var expected = Convert.FromHexString(credential.ApiKeySha256);
            if (CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                var identity = new ClaimsIdentity([new Claim(MerchantIdClaim, credential.MerchantId)], SchemeName);
                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
        }

        foreach (var credential in operators.CurrentValue.Credentials)
        {
            var expected = Convert.FromHexString(credential.ApiKeySha256);
            if (CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, credential.Name), new Claim(ClaimTypes.Role, OperatorRole)],
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }
}

public static class MerchantClaimsExtensions
{
    public static string GetMerchantId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ApiKeyAuthenticationHandler.MerchantIdClaim)
        ?? throw new InvalidOperationException("Request is not authenticated as a merchant.");
}
