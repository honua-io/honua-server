// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Server.Infrastructure.Logging;
using Serilog.Context;

namespace Honua.Server.Infrastructure.Middleware;

/// <summary>
/// Middleware to propagate or generate correlation IDs for distributed tracing across logs.
/// Supports both client-provided correlation IDs and server-generated fallbacks.
/// </summary>
/// <remarks>
/// Order of precedence for correlation ID:
/// 1. X-Correlation-ID header (client-provided)
/// 2. Activity.Current?.Id (OpenTelemetry trace ID)
/// 3. Generated Guid (fallback)
///
/// The correlation ID is:
/// - Added to the response X-Correlation-ID header
/// - Pushed to Serilog LogContext for all log entries
/// - Available via HttpContext.TraceIdentifier for framework integration
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private const string CorrelationIdLogProperty = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Determine correlation ID using order of precedence
        var correlationId = GetOrGenerateCorrelationId(context);

        // Set correlation ID in HttpContext for framework integration
        context.TraceIdentifier = correlationId;

        // Add correlation ID to response headers
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // Push correlation ID to Serilog LogContext for all log entries in this request
        using (LogContext.PushProperty(CorrelationIdLogProperty, correlationId))
        {
            // Log correlation ID establishment for debugging (verbose level)
            Log.CorrelationIdEstablished(_logger, correlationId, context.Request.Path);

            // Continue to next middleware
            await _next(context);
        }
    }

    /// <summary>
    /// Determines correlation ID using order of precedence:
    /// 1. X-Correlation-ID header (client-provided)
    /// 2. Activity.Current?.Id (OpenTelemetry trace ID)
    /// 3. Generated Guid (fallback)
    /// </summary>
    private static string GetOrGenerateCorrelationId(HttpContext context)
    {
        // 1. Check for client-provided correlation ID header
        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            var clientCorrelationId = headerValue.ToString().Trim();
            // Basic validation - ensure it's reasonable length and doesn't contain control characters
            if (clientCorrelationId.Length <= 128 &&
                !clientCorrelationId.Any(c => char.IsControl(c)))
            {
                return clientCorrelationId;
            }
        }

        // 2. Use OpenTelemetry Activity ID if available
        if (Activity.Current?.Id is not null)
        {
            return Activity.Current.Id;
        }

        // 3. Generate new correlation ID as fallback
        return Guid.NewGuid().ToString("D"); // Standard Guid format (32 digits separated by hyphens)
    }
}

/// <summary>
/// Extension methods for registering CorrelationIdMiddleware
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds correlation ID middleware to the application pipeline.
    /// Should be registered early in the pipeline to ensure all subsequent operations have correlation context.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for method chaining</returns>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}