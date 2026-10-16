using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Application.Idempotency;

namespace PaymentOrchestrator.Api.Idempotency;

/// <summary>
/// Makes POST endpoints safe to retry. The first request with a given Idempotency-Key is executed and its
/// response stored; later requests with the same key and payload get the stored response back.
/// </summary>
internal sealed class IdempotencyFilter(bool required) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    private const int MaxKeyLength = 255;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(key))
        {
            return required
                ? Problem(StatusCodes.Status400BadRequest, "idempotency.key_required", $"The {HeaderName} header is required.")
                : await next(context);
        }

        if (key.Length > MaxKeyLength)
        {
            return Problem(StatusCodes.Status400BadRequest, "idempotency.key_too_long", $"{HeaderName} must be at most {MaxKeyLength} characters.");
        }

        var merchantId = http.User.GetMerchantId();
        var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();
        var requestHash = await HashRequestAsync(http.Request);
        var ct = http.RequestAborted;

        var claim = await store.TryClaimAsync(merchantId, key, requestHash, ct);
        switch (claim)
        {
            case IdempotencyClaim.Completed completed:
                http.Response.Headers[ReplayedHeaderName] = "true";
                return Results.Content(completed.ResponseBody, "application/json", Encoding.UTF8, completed.StatusCode);
            case IdempotencyClaim.InProgress:
                return Problem(StatusCodes.Status409Conflict, "idempotency.in_progress", "A request with this key is still being processed. Retry shortly.");
            case IdempotencyClaim.PayloadMismatch:
                return Problem(StatusCodes.Status422UnprocessableEntity, "idempotency.payload_mismatch", "This key was already used with a different request.");
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            await store.ReleaseAsync(merchantId, key, CancellationToken.None);
            throw;
        }

        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        if (statusCode >= 500)
        {
            await store.ReleaseAsync(merchantId, key, CancellationToken.None);
            return result;
        }

        var value = (result as IValueHttpResult)?.Value;
        var jsonOptions = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var body = JsonSerializer.Serialize(value, jsonOptions);

        await store.CompleteAsync(merchantId, key, statusCode, body, CancellationToken.None);
        return result;
    }

    private static async Task<string> HashRequestAsync(HttpRequest request)
    {
        request.Body.Position = 0;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}\n"));

        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            sha.AppendData(buffer, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}

internal static class IdempotencyFilterExtensions
{
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder, bool required = false) =>
        builder.AddEndpointFilter(new IdempotencyFilter(required));
}
