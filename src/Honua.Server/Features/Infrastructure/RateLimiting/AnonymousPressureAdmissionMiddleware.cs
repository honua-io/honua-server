// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.RateLimiting;

internal sealed class AnonymousPressureAdmissionMiddleware(RequestDelegate next)
{
    private const int RetryAfterSeconds = 1;

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identities.Any(identity => identity.IsAuthenticated) ||
            HttpMethods.IsOptions(context.Request.Method) ||
            !IsExpensive(context) ||
            context.RequestServices.GetService<IRuntimeTunableAdmissionGate>() is not { } gate)
        {
            return next(context);
        }

        var pressure = gate.GetPressure();
        return IsUnderPressure(pressure)
            ? BackpressureResponseWriter.WriteAsync(context, BackpressureKind.Saturated, RetryAfterSeconds)
            : next(context);
    }

    internal static bool IsUnderPressure(DatabaseAdmissionPressure pressure)
        => pressure.Utilization >= 0.8 || pressure.QueuedWaiters > 0;

    private static bool IsExpensive(HttpContext context)
    {
        var operation = RequestTelemetryClassifier.ResolveOperation(context);
        if (HttpMethods.IsPost(context.Request.Method) && IsClassicOgcOperation(operation))
        {
            return true;
        }

        return operation switch
        {
            "query" or "related" or "edit" or "renderer" or "queryTopFeatures" or "queryDateBins" or
            "queryBins" or "queryH3" or "queryAnalytic" or "queryClusters" or "spatialJoin" or "queryBufferAggregate" or "queryDensity" or
            "export" or "exportImage" or "generateKml" or "identify" or "find" or
            "computeStatisticsHistograms" or "computeClassStatistics" or
            "aggregate" or "search" or "search.get" or "search.post" or "batch" or "create" or "update" or "delete" or "feature" or
            "areasAndLengths" or "lengths" or "buffer" or "simplify" or "project" or "intersect" or
            "union" or "clip" or "difference" or "coverage" or
            "solveClosestFacility" or "solveServiceArea" or "solveRoute" or "submitJob" or "execute" or
            "mcp.rpc" or "wfs.getfeature" or "wfs.getpropertyvalue" or "wfs.transaction" or
            "wfs.lockfeature" or "wms.getmap" or "wms.getfeatureinfo" or
            "wcs.getcoverage" or "wps.execute" or "wmts.getfeatureinfo" or
            "items" or "item" or "createReplica" or "extractChanges" or "synchronizeReplica" => true,
            "map" => PathEndsWithSegment(context, "/map"),
            "layers" => IsODataLayerFeatureRead(context),
            "attachments" => !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method),
            null => IsRecordsItemsRead(context) || IsSensorThingsEntityRequest(context),
            _ => operation.StartsWith("geospatial.v1.", StringComparison.Ordinal),
        };
    }

    private static bool PathEndsWithSegment(HttpContext context, string segment)
        => context.Request.Path.Value?.TrimEnd('/').EndsWith(segment, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsODataLayerFeatureRead(HttpContext context)
        => context.Request.Path.StartsWithSegments("/odata", StringComparison.OrdinalIgnoreCase) &&
           context.Request.Path.Value?.Contains("/Features", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSensorThingsEntityRequest(HttpContext context)
        => context.Request.Path.StartsWithSegments("/sta", StringComparison.OrdinalIgnoreCase, out var remaining) &&
           remaining.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    private static bool IsRecordsItemsRead(HttpContext context)
        => context.Request.Path.StartsWithSegments("/ogc/records/collections", StringComparison.OrdinalIgnoreCase) &&
           context.Request.Path.Value?.Contains("/items", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsClassicOgcOperation(string? operation)
        => operation is "wfs" or "wms" or "wcs" or "wps" ||
           operation?.StartsWith("wfs.", StringComparison.Ordinal) == true ||
           operation?.StartsWith("wms.", StringComparison.Ordinal) == true ||
           operation?.StartsWith("wcs.", StringComparison.Ordinal) == true ||
           operation?.StartsWith("wps.", StringComparison.Ordinal) == true;
}
