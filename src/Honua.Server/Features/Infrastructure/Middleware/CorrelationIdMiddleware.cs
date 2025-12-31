// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;
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

        // Enrich current Activity with correlation ID and request metadata
        EnrichCurrentActivity(context, correlationId);

        // Push correlation ID to Serilog LogContext for all log entries in this request
        using (LogContext.PushProperty(CorrelationIdLogProperty, correlationId))
        {
            // Log correlation ID establishment for debugging (verbose level)
            InfrastructureLog.CorrelationIdEstablished(_logger, correlationId, context.Request.Path);

            // Continue to next middleware
            await _next(context);
        }
    }

    /// <summary>
    /// Enriches the current Activity with correlation ID and request metadata for distributed tracing.
    /// </summary>
    private static void EnrichCurrentActivity(HttpContext context, string correlationId)
    {
        var activity = Activity.Current;
        if (activity == null) return;

        // Set correlation ID as a tag for trace correlation
        activity.SetTag(HonuaTelemetry.Tags.CorrelationId, correlationId);

        // Extract and set protocol-specific tags from route values
        var routeValues = context.Request.RouteValues;

        // Service ID from route
        if (routeValues.TryGetValue("serviceId", out var serviceId) && serviceId != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId.ToString());
        }
        else if (routeValues.TryGetValue("id", out var id) && id != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.ServiceId, id.ToString());
        }

        // Layer ID from route
        if (routeValues.TryGetValue("layerId", out var layerId) && layerId != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString());
        }

        // Collection ID for OGC API Features
        if (routeValues.TryGetValue("collectionId", out var collectionId) && collectionId != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.LayerId, collectionId.ToString());
        }

        // Tile coordinates for MVT tile requests
        if (routeValues.TryGetValue("z", out var z) && z != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.TileZ, z.ToString());
        }
        if (routeValues.TryGetValue("x", out var x) && x != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.TileX, x.ToString());
        }
        if (routeValues.TryGetValue("y", out var y) && y != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.TileY, y.ToString());
        }

        // Add baggage for downstream propagation
        activity.SetBaggage("correlation.id", correlationId);
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
        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out StringValues headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            string clientCorrelationId = headerValue.ToString().Trim();
            // Basic validation - ensure it's reasonable length and doesn't contain control characters
            if (clientCorrelationId.Length <= 128 &&
                !HasControlCharacters(clientCorrelationId))
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

    /// <summary>
    /// Checks if a string contains control characters without using LINQ in async context
    /// </summary>
    private static bool HasControlCharacters(string input)
    {
        for (int i = 0; i < input.Length; i++)
        {
            if (char.IsControl(input[i]))
                return true;
        }
        return false;
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
