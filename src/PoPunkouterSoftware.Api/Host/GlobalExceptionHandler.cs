using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// Global exception handler — converts unhandled exceptions to RFC 7807 Problem Details.
/// </summary>
public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        logger.LogError(
            exception,
            "Unhandled exception. TraceId: {TraceId}, Path: {Path}",
            traceId,
            httpContext.Request.Path);

        var statusCode = exception switch
        {
            ArgumentNullException or ArgumentException => StatusCodes.Status400BadRequest,
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            InvalidOperationException => StatusCodes.Status409Conflict,
            TaskCanceledException or TimeoutException => StatusCodes.Status408RequestTimeout,
            _ => StatusCodes.Status500InternalServerError
        };

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = "An error occurred while processing the request.",
            Detail = environment.IsDevelopment() ? exception.ToString() : "An internal error occurred. See server logs for details.",
            Instance = httpContext.Request.Path
        };

        problemDetails.Extensions["traceId"] = traceId;

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(
            problemDetails,
            (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json",
            cancellationToken);

        return true;
    }
}

