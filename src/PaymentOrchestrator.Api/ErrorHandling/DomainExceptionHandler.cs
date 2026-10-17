using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentOrchestrator.Application.Payments;
using PaymentOrchestrator.Domain.Common;

namespace PaymentOrchestrator.Api.ErrorHandling;

internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            DomainException { Code: "payment.invalid_transition" } e => (StatusCodes.Status409Conflict, e.Code, e.Message),
            DomainException e => (StatusCodes.Status422UnprocessableEntity, e.Code, e.Message),
            ProviderOperationException { Code: "outcome_pending" } e => (StatusCodes.Status409Conflict, e.Code, e.Message),
            ProviderOperationException { Retryable: true } e => (StatusCodes.Status503ServiceUnavailable, e.Code, e.Message),
            ProviderOperationException e => (StatusCodes.Status422UnprocessableEntity, e.Code, e.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency_conflict",
                "The payment was modified by another request. Retry with the latest state."),
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = "Request could not be processed",
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }
}
