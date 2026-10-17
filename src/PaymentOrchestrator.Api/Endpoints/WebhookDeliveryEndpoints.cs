using System.Collections.Concurrent;
using System.Security.Claims;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Application.Webhooks;
using PaymentOrchestrator.Infrastructure.Webhooks;

namespace PaymentOrchestrator.Api.Endpoints;

internal static class WebhookDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapWebhookDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/webhook-deliveries").WithTags("Webhook deliveries").RequireAuthorization();

        group.MapGet("/", async (string? status, int? limit, ClaimsPrincipal user, IWebhookDeliveryStore store, CancellationToken ct) =>
            {
                WebhookDeliveryStatus? filter = null;
                if (status is not null)
                {
                    if (!Enum.TryParse<WebhookDeliveryStatus>(status, ignoreCase: true, out var parsed))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "status must be pending, delivered or dead.");
                    }

                    filter = parsed;
                }

                return Results.Ok(await store.ListAsync(user.GetMerchantId(), filter, limit ?? 50, ct));
            })
            .WithName("ListWebhookDeliveries")
            .Produces<IReadOnlyList<WebhookDeliveryResponse>>();

        group.MapPost("/{id:guid}/replay", async (Guid id, ClaimsPrincipal user, IWebhookDeliveryStore store, CancellationToken ct) =>
                await store.ReplayAsync(user.GetMerchantId(), id, ct) is { } delivery ? Results.Ok(delivery) : Results.NotFound())
            .WithName("ReplayWebhookDelivery")
            .Produces<WebhookDeliveryResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    public sealed record ReceivedWebhook(DateTimeOffset ReceivedAt, string? EventId, bool SignatureValid, string Body);

    /// <summary>
    /// Development stand-in for a merchant's server: verifies the signature and keeps the last 50 webhooks so the
    /// end-to-end flow can be watched locally.
    /// </summary>
    public static IEndpointRouteBuilder MapDevWebhookReceiver(this IEndpointRouteBuilder app)
    {
        var received = new ConcurrentQueue<ReceivedWebhook>();
        var group = app.MapGroup("/dev/webhook-receiver").WithTags("Simulator (development)");

        group.MapPost("/{merchantId}", async (string merchantId, HttpRequest request, IMerchantWebhookDirectory directory, TimeProvider clock) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var endpoint = directory.Find(merchantId);

            var valid = endpoint is not null
                && long.TryParse(request.Headers[WebhookSigner.TimestampHeader], out var timestamp)
                && WebhookSigner.Verify(endpoint.Secret, timestamp, body, request.Headers[WebhookSigner.SignatureHeader].ToString());

            received.Enqueue(new ReceivedWebhook(clock.GetUtcNow(), request.Headers[WebhookSigner.IdHeader], valid, body));
            while (received.Count > 50)
            {
                received.TryDequeue(out _);
            }

            return valid ? Results.Ok() : Results.Unauthorized();
        });

        group.MapGet("/", () => Results.Ok(received.Reverse()));

        return app;
    }
}
