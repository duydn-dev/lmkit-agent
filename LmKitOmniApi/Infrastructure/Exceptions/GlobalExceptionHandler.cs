using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Infrastructure.Exceptions;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            exception, "Exception occurred: {Message}", exception.Message);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Server Error",
            Detail = "An unexpected error occurred processing your request."
        };

        if (exception is UnauthorizedAccessException)
        {
            problemDetails.Status = StatusCodes.Status401Unauthorized;
            problemDetails.Title = "Unauthorized";
            problemDetails.Detail = exception.Message;
        }
        else if (exception is ArgumentException || exception is InvalidOperationException)
        {
            // Do not surface exception.Message to clients: it can leak internal detail
            // (file paths, SQL fragments, config values, invariant text). The full
            // exception is already logged server-side above.
            problemDetails.Status = StatusCodes.Status400BadRequest;
            problemDetails.Title = "Bad Request";
            problemDetails.Detail = "The request was invalid or could not be processed.";
        }

        httpContext.Response.StatusCode = problemDetails.Status.Value;

        await httpContext.Response
            .WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
