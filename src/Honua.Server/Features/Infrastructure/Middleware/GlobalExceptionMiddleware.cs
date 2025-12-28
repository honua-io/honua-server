// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;
using InfrastructureLog = Honua.Server.Features.Infrastructure.Logging.Log;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Middleware to catch unhandled exceptions and convert them to standardized error responses
/// based on the request protocol (GeoServices, OData, etc.).
/// </summary>
/// <remarks>
/// This middleware ensures consistent error response formats across all protocols
/// and prevents sensitive error details from being exposed to clients.
/// It also logs all unhandled exceptions with correlation IDs for tracking.
/// </remarks>
internal sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<GlobalExceptionMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // Log the unhandled exception with correlation ID
            InfrastructureLog.UnhandledException(_logger, context.Request.Path, context.TraceIdentifier, ex.Message, ex);

            // Check if response has already been started
            if (context.Response.HasStarted)
            {
                // Cannot modify response after it has started - rethrow to let framework handle
                throw;
            }

            // Convert to standardized error response based on protocol
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Determine appropriate status code based on exception type
        var (statusCode, title, detail) = MapExceptionToResponse(exception);

        // Use ProtocolErrorWriter to format response appropriately for the protocol
        await ProtocolErrorWriter.WriteErrorAsync(context, statusCode, title, detail);
    }

    private static (int StatusCode, string Title, string Detail) MapExceptionToResponse(Exception exception)
    {
        return exception switch
        {
            ArgumentNullException => (400, "Bad Request", "Missing required parameter."),
            ArgumentException => (400, "Bad Request", "Invalid request parameters."),
            InvalidOperationException => (400, "Bad Request", "Invalid operation."),
            UnauthorizedAccessException => (401, "Unauthorized", "Access denied."),
            NotSupportedException => (405, "Method Not Allowed", "Operation not supported."),
            OperationCanceledException => (408, "Request Timeout", "The request was cancelled."),
            TimeoutException => (408, "Request Timeout", "The request timed out."),
            _ => (500, "Internal Server Error", "An unexpected error occurred.")
        };
    }
}

/// <summary>
/// Extension methods for GlobalExceptionMiddleware
/// </summary>
internal static class GlobalExceptionMiddlewareExtensions
{
    /// <summary>
    /// Adds global exception handling middleware to the pipeline
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseGlobalExceptionHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<GlobalExceptionMiddleware>();
    }
}