using PaymentOrchestrator.Application.Providers;
using PaymentOrchestrator.Application.Webhooks;

namespace PaymentOrchestrator.Api.Endpoints;

internal static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapProviderWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/{provider}", async (string provider, HttpRequest request, ProviderWebhookService service, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync(ct);
                var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

                try
                {
                    return await service.HandleAsync(provider, headers, body, ct) switch
                    {
                        WebhookHandlingResult.UnknownProvider => Results.NotFound(),
                        var result => Results.Ok(new { result = result.ToString().ToLowerInvariant() }),
                    };
                }
                catch (WebhookSignatureException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Invalid webhook signature.");
                }
            })
            .WithTags("Webhooks")
            .WithName("ReceiveProviderWebhook")
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }
}
