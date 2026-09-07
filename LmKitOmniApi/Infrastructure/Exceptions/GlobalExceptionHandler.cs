using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Infrastructure.Exceptions;

/// <summary>
/// Last-chance handler for exceptions that escape the pipeline.
///
/// Three properties matter here and each of them used to be wrong:
///
/// 1. SERVER FAULTS MUST LOOK LIKE SERVER FAULTS. Every
///    <see cref="InvalidOperationException"/> was mapped to 400 Bad Request. This codebase
///    throws that type for genuine server-side misconfiguration and broken invariants —
///    "SearXNG base URL is not configured", "AiModels:MaxDownloadBytes must be greater than
///    zero", a widget signing key that will not load, and every EF
///    <c>Single()</c>/<c>First()</c> violation — so real outages were reported to clients
///    (and to 5xx dashboards and SLO alerts) as caller mistakes and simply vanished.
///    Only genuinely caller-shaped failures (<see cref="ArgumentException"/> and friends,
///    which model validation raises for bad input) stay in 4xx.
///
/// 2. NEVER WRITE A BODY ONTO A STARTED RESPONSE. <c>WriteAsJsonAsync</c> was called
///    unconditionally. On an SSE / streaming endpoint the headers are long gone by the time
///    a mid-stream failure surfaces, so the write itself throws
///    <see cref="InvalidOperationException"/> ("StatusCode cannot be set, response has
///    already started") from inside the error handler — turning a recoverable stream fault
///    into a connection-level crash. <see cref="HttpResponse.HasStarted"/> is now checked.
///
/// 3. A CLIENT HANGING UP IS NOT AN ERROR. A closed SSE tab / navigated-away browser
///    aborts the request, surfacing as <see cref="OperationCanceledException"/>. Logging
///    those at Error floods the error budget with user behaviour, so they are logged at
///    Information and produce no response body.
/// </summary>
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
        if (IsClientDisconnect(httpContext, exception))
        {
            _logger.LogInformation(
                "Request aborted by the client before completion: {Method} {Path}",
                httpContext.Request.Method,
                httpContext.Request.Path);
            return true;
        }

        var problemDetails = Classify(exception);
        var status = problemDetails.Status!.Value;

        if (status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "Unhandled server fault on {Method} {Path}: {Message}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                exception.Message);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Rejected request {Method} {Path} with {Status}: {Message}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                status,
                exception.Message);
        }

        // Streaming endpoints (SSE, chunked downloads) have already flushed headers — and
        // often a partial body — by the time a failure reaches here. Touching StatusCode or
        // writing a ProblemDetails document then throws from inside the handler.
        if (httpContext.Response.HasStarted)
        {
            _logger.LogWarning(
                "Response for {Method} {Path} had already started; the {Status} ProblemDetails body could not be written.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                status);
            return true;
        }

        httpContext.Response.StatusCode = status;

        try
        {
            await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        }
        catch (Exception writeFailure) when (writeFailure is OperationCanceledException
            || writeFailure is IOException
            || writeFailure is ObjectDisposedException)
        {
            // The connection died while we were reporting the failure. Nothing left to do —
            // the original exception is already logged above.
            _logger.LogInformation(
                "Could not deliver the error response for {Method} {Path}: {Reason}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                writeFailure.GetType().Name);
        }

        return true;
    }

    /// <summary>
    /// A cancellation that the CLIENT caused. Server-side timeouts also surface as
    /// <see cref="OperationCanceledException"/>, so the aborted-request token is what
    /// separates "the user closed the tab" from "we gave up" — the latter is a real fault.
    /// </summary>
    private static bool IsClientDisconnect(HttpContext httpContext, Exception exception) =>
        exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested;

    /// <summary>
    /// Maps an exception to a wire status. Detail text is deliberately generic: the full
    /// exception is logged server-side, and messages leak internal detail (file paths, SQL
    /// fragments, config values, invariant text).
    /// </summary>
    internal static ProblemDetails Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException => new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = exception.Message
        },

        // Caller-shaped failures: model binding / explicit argument validation.
        // ArgumentNullException and ArgumentOutOfRangeException derive from ArgumentException.
        ArgumentException => new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad Request",
            Detail = "The request was invalid or could not be processed."
        },

        BadHttpRequestException badRequest => new ProblemDetails
        {
            Status = badRequest.StatusCode,
            Title = "Bad Request",
            Detail = "The request was invalid or could not be processed."
        },

        // Everything else — InvalidOperationException included — is a server fault.
        _ => new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Server Error",
            Detail = "An unexpected error occurred processing your request."
        }
    };
}
