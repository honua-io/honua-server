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

        // Enrich current Activity with correlation ID early for tracing/log correlation.
        EnrichActivityWithCorrelationId(correlationId);

        // Push correlation ID to Serilog LogContext for all log entries in this request
        using (LogContext.PushProperty(CorrelationIdLogProperty, correlationId))
        {
            // Log correlation ID establishment for debugging (verbose level)
            InfrastructureLog.CorrelationIdEstablished(_logger, correlationId, context.Request.Path);

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

        var protocol = ResolveProtocol(context.Request.Path);
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            activity.SetTag(HonuaTelemetry.Tags.Protocol, protocol);
        }

        var operation = ResolveOperation(context);
        if (!string.IsNullOrWhiteSpace(operation))
        {
            activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
        }

        // Extract and set protocol-specific tags from route values
        var routeValues = context.Request.RouteValues;

        // Service ID from route
        if (routeValues.TryGetValue("serviceId", out var serviceId) && serviceId != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId.ToString());
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
    }

    private static string? ResolveProtocol(PathString path)
    {
        var value = path.Value ?? string.Empty;

        if (value.Contains("/FeatureServer", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/tiles", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.FeatureServer;
        }

        if (value.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/collections", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.OgcFeatures;
        }

        if (value.StartsWith("/odata", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.OData;
        }

        if (value.StartsWith("/import", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.Import;
        }

        if (value.Contains("/admin", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.Admin;
        }

        if (value.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/alive", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.Health;
        }

        if (value.Contains("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.Monitoring;
        }

        return null;
    }

    private static string? ResolveOperation(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        if (path.Contains("/FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/queryRelatedRecords", StringComparison.OrdinalIgnoreCase))
            {
                return "related";
            }

            if (path.Contains("/applyEdits", StringComparison.OrdinalIgnoreCase))
            {
                return "edit";
            }

            if (path.Contains("/generateRenderer", StringComparison.OrdinalIgnoreCase))
            {
                return "renderer";
            }

            if (path.Contains("/query", StringComparison.OrdinalIgnoreCase))
            {
                return "query";
            }

            return "metadata";
        }

        if (path.StartsWith("/tiles", StringComparison.OrdinalIgnoreCase))
        {
            return "tile";
        }

        if (path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(path, "/odata", StringComparison.OrdinalIgnoreCase))
            {
                return "service_document";
            }

            if (path.EndsWith("/$metadata", StringComparison.OrdinalIgnoreCase))
            {
                return "metadata";
            }

            if (path.Contains("/$batch", StringComparison.OrdinalIgnoreCase))
            {
                return "batch";
            }

            if (path.Contains("/$apply", StringComparison.OrdinalIgnoreCase))
            {
                return "aggregate";
            }

            if (path.Contains("/$search", StringComparison.OrdinalIgnoreCase))
            {
                return "search";
            }

            if (path.Contains("/Layers", StringComparison.OrdinalIgnoreCase))
            {
                return "layers";
            }

            if (path.Contains("/Features", StringComparison.OrdinalIgnoreCase))
            {
                return method.ToUpperInvariant() switch
                {
                    "POST" => "create",
                    "PATCH" => "update",
                    "PUT" => "update",
                    "DELETE" => "delete",
                    _ => "query"
                };
            }
        }

        if (path.StartsWith("/ogc/features", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/conformance", StringComparison.OrdinalIgnoreCase))
            {
                return "conformance";
            }

            if (string.Equals(path, "/ogc/features", StringComparison.OrdinalIgnoreCase))
            {
                return "landing";
            }

            if (path.EndsWith("/collections", StringComparison.OrdinalIgnoreCase))
            {
                return "collections";
            }

            if (path.Contains("/queryables", StringComparison.OrdinalIgnoreCase))
            {
                return "queryables";
            }

            if (path.Contains("/items/batch", StringComparison.OrdinalIgnoreCase))
            {
                return "batch";
            }

            if (path.Contains("/items/", StringComparison.OrdinalIgnoreCase))
            {
                return method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    ? "feature"
                    : method.ToUpperInvariant() switch
                    {
                        "POST" => "create",
                        "PUT" => "update",
                        "PATCH" => "update",
                        "DELETE" => "delete",
                        _ => "feature"
                    };
            }

            if (path.Contains("/items", StringComparison.OrdinalIgnoreCase))
            {
                return method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    ? "query"
                    : method.ToUpperInvariant() switch
                    {
                        "POST" => "create",
                        "PUT" => "update",
                        "PATCH" => "update",
                        "DELETE" => "delete",
                        _ => "query"
                    };
            }
        }

        if (path.StartsWith("/ogc/tiles", StringComparison.OrdinalIgnoreCase))
        {
            return "tiles";
        }

        return null;
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
