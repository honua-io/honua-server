// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Serilog.Context;
using InfrastructureLog = Honua.Server.Features.Infrastructure.Logging.Log;

namespace Honua.Server.Features.Infrastructure.Middleware;

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
/// - Set as a tag on the current Activity for distributed tracing
/// </remarks>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private const string CorrelationIdLogProperty = "CorrelationId";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<CorrelationIdMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        // Determine correlation ID using order of precedence
        string correlationId = GetOrGenerateCorrelationId(context);

        // Set correlation ID in HttpContext for framework integration
        context.TraceIdentifier = correlationId;

        // Add correlation ID to response headers
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // Enrich current Activity with correlation ID early for tracing/log correlation.
        EnrichActivityWithCorrelationId(correlationId);

        // Push correlation ID to Serilog LogContext for all log entries in this request
        using (LogContext.PushProperty(CorrelationIdLogProperty, correlationId))
        {
            // Log correlation ID establishment for debugging (verbose level)
            var requestPath = context.Request.Path.Value ?? string.Empty;
            InfrastructureLog.CorrelationIdEstablished(_logger, correlationId, requestPath);

            try
            {
                // Continue to next middleware
                await _next(context);
            }
            finally
            {
                // Populate standardized tags once routing has resolved route values.
                EnrichActivityWithRequestContext(context);
            }
        }
    }

    /// <summary>
    /// Enriches the current Activity with correlation ID and baggage for distributed tracing.
    /// </summary>
    private static void EnrichActivityWithCorrelationId(string correlationId)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        // Set correlation ID as a tag for trace correlation
        activity.SetTag(HonuaTelemetry.Tags.CorrelationId, correlationId);
        activity.SetBaggage("correlation.id", correlationId);
    }

    /// <summary>
    /// Enriches the current Activity with request metadata for distributed tracing.
    /// </summary>
    private static void EnrichActivityWithRequestContext(HttpContext context)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        var protocol = RequestTelemetryClassifier.ResolveProtocol(context.Request.Path);
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            activity.SetTag(HonuaTelemetry.Tags.Protocol, protocol);
        }

        var operation = RequestTelemetryClassifier.ResolveOperation(context);
        if (!string.IsNullOrWhiteSpace(operation))
        {
            activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
        }

        // Extract and set protocol-specific tags from route values
        var routeValues = context.Request.RouteValues;

        // Service ID from route
        if (TryGetRouteValue(routeValues, out var serviceId, "serviceId", "id"))
        {
            activity.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        }

        // Layer ID from route
        if (TryGetRouteValue(routeValues, out var layerId, "layerId"))
        {
            activity.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        }

        // Collection ID for OGC API Features
        if (TryGetRouteValue(routeValues, out var collectionId, "collectionId"))
        {
            activity.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);
        }

        if (TryGetRouteValue(routeValues, out var taskName, "taskName"))
        {
            activity.SetTag(HonuaTelemetry.Tags.TaskName, taskName);
        }

        if (TryGetRouteValue(routeValues, out var jobId, "jobId"))
        {
            activity.SetTag(HonuaTelemetry.Tags.JobId, jobId);
        }

        if (TryGetRouteValue(routeValues, out var parameterName, "paramName"))
        {
            activity.SetTag(HonuaTelemetry.Tags.ParameterName, parameterName);
        }

        // Tile coordinates for MVT tile requests
        if (TryGetRouteValue(routeValues, out var z, "z", "level"))
        {
            activity.SetTag(HonuaTelemetry.Tags.TileZ, z);
        }
        if (TryGetRouteValue(routeValues, out var x, "x", "col"))
        {
            activity.SetTag(HonuaTelemetry.Tags.TileX, x);
        }
        if (TryGetRouteValue(routeValues, out var y, "y", "row"))
        {
            activity.SetTag(HonuaTelemetry.Tags.TileY, y);
        }
    }

    private static bool TryGetRouteValue(
        RouteValueDictionary routeValues,
        out string? value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (routeValues.TryGetValue(key, out var routeValue) && routeValue != null)
            {
                value = routeValue.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Determines correlation ID using order of precedence:
    /// 1. X-Correlation-ID header (client-provided)
    /// 2. Activity.Current?.Id (OpenTelemetry trace ID)
    /// 3. Generated Guid (fallback)
    /// </summary>
    private static string GetOrGenerateCorrelationId(HttpContext context)
    {
        // 1. Check for client-provided correlation ID header.
        //    Accept only alphanumeric, hyphen, underscore, dot, colon — enough to cover
        //    UUID, trace-id, and common custom forms without allowing log-injection
        //    sequences (CR/LF, control chars, quotes, whitespace, etc.). Cap at 64 chars.
        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out StringValues headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            string clientCorrelationId = headerValue.ToString().Trim();
            if (clientCorrelationId.Length <= 64 &&
                IsSafeCorrelationIdForm(clientCorrelationId))
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

    // Accepts only characters safe for structured logging sinks and trace-id
    // propagation: ASCII letters/digits, hyphen, underscore, dot, colon.
    // Rejects control characters, whitespace, quotes, and anything outside that
    // set to stop log-injection via the X-Correlation-ID header.
    private static bool IsSafeCorrelationIdForm(string input)
    {
        if (input.Length == 0) return false;
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            var isAllowed = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == ':';
            if (!isAllowed) return false;
        }
        return true;
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
