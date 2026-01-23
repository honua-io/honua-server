// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
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
internal sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger,
    IHostEnvironment environment)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<GlobalExceptionMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly bool _includeDebugDetails = environment?.IsDevelopment() ?? false;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; skip logging and response shaping.
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

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Add correlation ID header for traceability
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;

        // Use standardized error handling system
        var errorResponse = StandardErrorResponse.FromException(exception, _includeDebugDetails);

        // Prepare formatter options with debug info if enabled
        var options = new ErrorResponseFormatterOptions
        {
            IncludeDebugInfo = _includeDebugDetails
        };

        // Handle ServiceUnavailable with Retry-After header
        if (exception is ServiceUnavailableException serviceEx && serviceEx.RetryAfterSeconds.HasValue)
        {
            context.Response.Headers["Retry-After"] = serviceEx.RetryAfterSeconds.Value.ToString();
            options = new ErrorResponseFormatterOptions
            {
                IncludeAdditionalDetails = options.IncludeAdditionalDetails,
                IncludeDebugInfo = options.IncludeDebugInfo,
                ContentType = options.ContentType,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Retry-After"] = serviceEx.RetryAfterSeconds.Value.ToString()
                }
            };
        }

        // Use standardized error formatter for protocol-appropriate response
        await StandardErrorResponseFormatter.WriteErrorAsync(context, errorResponse, options);
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
