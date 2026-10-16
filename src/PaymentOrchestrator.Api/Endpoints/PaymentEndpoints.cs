using System.Security.Claims;
using PaymentOrchestrator.Api.Authentication;
using PaymentOrchestrator.Application.Payments;

namespace PaymentOrchestrator.Api.Endpoints;

internal static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/payments")
            .WithTags("Payments")
            .RequireAuthorization();

        group.MapPost("/", async (CreatePaymentRequest request, ClaimsPrincipal user, PaymentService service, CancellationToken ct) =>
            {
                var payment = await service.CreateAsync(user.GetMerchantId(), request, ct);
                return Results.Created($"/v1/payments/{payment.Id}", payment);
            })
            .WithName("CreatePayment")
            .Produces<PaymentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, PaymentService service, CancellationToken ct) =>
                await service.GetAsync(user.GetMerchantId(), id, ct) is { } payment
                    ? Results.Ok(payment)
                    : Results.NotFound())
            .WithName("GetPayment")
            .Produces<PaymentResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/capture", async (Guid id, ClaimsPrincipal user, PaymentService service, CancellationToken ct) =>
                ToResult(await service.CaptureAsync(user.GetMerchantId(), id, ct)))
            .WithName("CapturePayment")
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, PaymentService service, CancellationToken ct) =>
                ToResult(await service.CancelAsync(user.GetMerchantId(), id, ct)))
            .WithName("CancelPayment")
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/refunds", async (Guid id, RefundPaymentRequest request, ClaimsPrincipal user, PaymentService service, CancellationToken ct) =>
                ToResult(await service.RefundAsync(user.GetMerchantId(), id, request, ct)))
            .WithName("RefundPayment")
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static IResult ToResult(PaymentResponse? payment) =>
        payment is null ? Results.NotFound() : Results.Ok(payment);
}
