// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Serilog.Context;
using InfrastructureLog = Honua.Infrastructure.Logging.Log;

namespace Honua.Infrastructure.Middleware;

internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private const string CorrelationIdLogProperty = "CorrelationId";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<CorrelationIdMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = GetOrGenerateCorrelationId(context);
        context.TraceIdentifier = correlationId;
        AttachCorrelationIdHeader(context, correlationId);
        EnrichActivityWithCorrelationId(correlationId);

        using (LogContext.PushProperty(CorrelationIdLogProperty, correlationId))
        {
            var requestPath = context.Request.Path.Value ?? string.Empty;
            InfrastructureLog.CorrelationIdEstablished(_logger, correlationId, requestPath);

            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                await _next(context);
            }
            finally
            {
                EnrichActivityWithRequestContext(context);
                RecordServingRequestMetric(context, startTimestamp);
            }
        }
    }

    private static void AttachCorrelationIdHeader(HttpContext context, string correlationId)
    {
        context.Response.Headers[CorrelationIdHeader] = correlationId;
        context.Response.OnStarting(static state =>
        {
            var (httpContext, id) = ((HttpContext, string))state;
            httpContext.Response.Headers.TryAdd(CorrelationIdHeader, id);
            return Task.CompletedTask;
        }, (context, correlationId));
    }

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

    // Health probes and the metrics scrape are excluded from the serving histogram, because their volume would dilute the error-budget denominator.
    private static void RecordServingRequestMetric(HttpContext context, long startTimestamp)
    {
        var protocol = RequestTelemetryClassifier.ResolveProtocol(context.Request.Path);
        if (string.IsNullOrWhiteSpace(protocol) ||
            Array.IndexOf(HonuaTelemetry.Protocols.NonServingPlane, protocol) >= 0)
        {
            return;
        }

        var operation = RequestTelemetryClassifier.ResolveOperation(context);
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        // gRPC answers HTTP 200 for failed calls; the classifier maps grpc-status to the HTTP
        // equivalent so gRPC errors count as errors in the histogram and the ops-health reservoir.
        var statusCode = RequestTelemetryClassifier.ResolveServingStatusCode(context, protocol);
        HonuaTelemetry.RecordServingRequest(protocol, operation, statusCode, elapsedMs);
    }

    private static bool TryGetRouteValue(
        RouteValueDictionary routeValues,
        out string? value,
        params string[] keys)
    {
        var keyIndex = 0;
        while (keyIndex < keys.Length)
        {
            if (routeValues.TryGetValue(keys[keyIndex], out var routeValue) &&
                routeValue is not null)
            {
                value = routeValue.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }

            keyIndex++;
        }

        value = null;
        return false;
    }

    private static string GetOrGenerateCorrelationId(HttpContext context)
    {
        // 1. Check for client-provided correlation ID header.
        //    Accept only alphanumeric, hyphen, underscore, dot, colon — enough to cover
        //    UUID, trace-id, and common custom forms without allowing log-injection
        //    sequences (CR/LF, control chars, quotes, whitespace, etc.). Cap at 64 chars.
        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out StringValues headerValue) &&
            NormalizeCorrelationId(headerValue.ToString()) is { } clientCorrelationId)
        {
            return clientCorrelationId;
        }

        // 2. Use OpenTelemetry Activity ID if available
        if (Activity.Current?.Id is not null)
        {
            return Activity.Current.Id;
        }

        // 3. Generate new correlation ID as fallback
        return Guid.NewGuid().ToString("D"); // Standard Guid format (32 digits separated by hyphens)
    }

    internal static string? NormalizeCorrelationId(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmed = rawValue.Trim();
        return trimmed.Length <= 64 && IsSafeCorrelationIdForm(trimmed) ? trimmed : null;
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

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }

    public static IApplicationBuilder UseGrpcServingStatusCapture(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                RequestTelemetryClassifier.CaptureGrpcStatus(context);
            }
        });
    }
}
