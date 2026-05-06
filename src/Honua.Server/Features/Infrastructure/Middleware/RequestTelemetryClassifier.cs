// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ServiceDefaults;

namespace Honua.Server.Features.Infrastructure.Middleware;

internal static class RequestTelemetryClassifier
{
    internal const string OperationItemKey = "__honua.telemetry.operation";

    private const string PMTilesProxyPathPrefix = "/api/v1/tiles/pmtiles";

    internal static string? ResolveProtocol(PathString path)
    {
        var value = path.Value ?? string.Empty;

        if (value.StartsWith("/rest/services/Utilities/PrintingTools/GPServer", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.PrintingTools;
        }

        // Check the PMTiles range-proxy surface before the broader "/admin" /
        // "/tiles" fallbacks below so range traffic is tagged as PMTiles instead
        // of being silently dropped or absorbed into the FeatureServer bucket.
        if (StartsWithPathSegment(value, PMTilesProxyPathPrefix))
        {
            return HonuaTelemetry.Protocols.PMTiles;
        }

        if (StartsWithPathSegment(value, "/terrain"))
        {
            return HonuaTelemetry.Protocols.Terrain;
        }

        if (StartsWithPathSegment(value, "/ogc/tiles"))
        {
            return HonuaTelemetry.Protocols.OgcTiles;
        }

        if (StartsWithPathSegment(value, "/ogc/maps"))
        {
            return HonuaTelemetry.Protocols.OgcMaps;
        }

        if (StartsWithPathSegment(value, "/ogc/coverages"))
        {
            return HonuaTelemetry.Protocols.OgcCoverages;
        }

        if (StartsWithPathSegment(value, "/ogc/processes"))
        {
            return HonuaTelemetry.Protocols.OgcProcesses;
        }

        if (StartsWithPathSegment(value, "/wfs"))
        {
            return HonuaTelemetry.Protocols.Wfs20;
        }

        if (StartsWithPathSegment(value, "/stac"))
        {
            return HonuaTelemetry.Protocols.Stac;
        }

        if (value.StartsWith("/ogc/services/", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("/wmts", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.OgcTiles;
        }

        if (value.StartsWith("/ogc/services/", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("/wms", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.OgcMaps;
        }

        if (value.StartsWith("/ogc/services/", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("/wcs", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.Wcs20;
        }

        if (value.StartsWith("/rest/services/", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("/MapServer/", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("/wmts", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.OgcTiles;
        }

        if (value.StartsWith("/rest/services/", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("/MapServer/", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("/wms", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.OgcMaps;
        }

        if (value.StartsWith("/rest/services/", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("/ImageServer/", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("/wcs", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.Wcs20;
        }

        if (value.Contains("/GeometryServer", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.GeometryService;
        }

        if (value.Contains("/NAServer", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.NAServer;
        }

        if (value.Contains("/GPServer", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.GPServer;
        }

        if (value.Contains("/ImageServer", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.ImageServer;
        }

        if (value.Contains("/MapServer", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.MapServer;
        }

        if (value.Contains("/FeatureServer", StringComparison.OrdinalIgnoreCase) ||
            StartsWithPathSegment(value, "/tiles"))
        {
            return HonuaTelemetry.Protocols.FeatureServer;
        }

        if (StartsWithPathSegment(value, "/ogc/features") ||
            StartsWithPathSegment(value, "/collections"))
        {
            return HonuaTelemetry.Protocols.OgcFeatures;
        }

        if (StartsWithPathSegment(value, "/odata"))
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

        if (value.Contains("/streaming", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaTelemetry.Protocols.Streaming;
        }

        return null;
    }

    internal static string? ResolveOperation(HttpContext context)
    {
        if (context.Items.TryGetValue(OperationItemKey, out var operation) &&
            operation is string operationName &&
            !string.IsNullOrWhiteSpace(operationName))
        {
            return operationName;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        if (StartsWithPathSegment(path, PMTilesProxyPathPrefix))
        {
            return string.Equals(method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase)
                ? "pmtiles.head"
                : "pmtiles.range";
        }

        if (StartsWithPathSegment(path, "/terrain"))
        {
            return path.EndsWith("/tile.json", StringComparison.OrdinalIgnoreCase)
                ? "terrain.metadata"
                : "terrain.tile";
        }

        if (path.StartsWith("/rest/services/Utilities/PrintingTools/GPServer", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePrintingToolsOperation(path);
        }

        if (path.Contains("/NAServer", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveNAServerOperation(path);
        }

        if (path.Contains("/GPServer", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveGpServerOperation(path);
        }

        if (StartsWithPathSegment(path, "/wfs"))
        {
            return ResolveQueryRequestOperation(context, "wfs");
        }

        if (path.StartsWith("/ogc/services/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/wmts", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveMapServerOgcOperation(context, "wmts");
        }

        if (path.StartsWith("/ogc/services/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/wms", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveMapServerOgcOperation(context, "wms");
        }

        if (path.StartsWith("/ogc/services/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/wcs", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveQueryRequestOperation(context, "wcs");
        }

        if (path.Contains("/FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/queryRelatedRecords", StringComparison.OrdinalIgnoreCase))
            {
                return "related";
            }

            if (path.Contains("/applyEdits", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/addFeatures", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/updateFeatures", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/deleteFeatures", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/append", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/calculate", StringComparison.OrdinalIgnoreCase))
            {
                return "edit";
            }

            if (path.Contains("/generateRenderer", StringComparison.OrdinalIgnoreCase))
            {
                return "renderer";
            }

            if (path.Contains("/queryTopFeatures", StringComparison.OrdinalIgnoreCase))
            {
                return "queryTopFeatures";
            }

            if (path.Contains("/queryDateBins", StringComparison.OrdinalIgnoreCase))
            {
                return "queryDateBins";
            }

            if (path.Contains("/queryBins", StringComparison.OrdinalIgnoreCase))
            {
                return "queryBins";
            }

            if (path.Contains("/getEstimates", StringComparison.OrdinalIgnoreCase))
            {
                return "getEstimates";
            }

            if (path.Contains("/queryDomains", StringComparison.OrdinalIgnoreCase))
            {
                return "queryDomains";
            }

            if (path.Contains("/relationships", StringComparison.OrdinalIgnoreCase))
            {
                return "relationships";
            }

            if (path.Contains("/validateSQL", StringComparison.OrdinalIgnoreCase))
            {
                return "validateSQL";
            }

            if (path.Contains("/queryH3", StringComparison.OrdinalIgnoreCase))
            {
                return "queryH3";
            }

            if (path.Contains("/queryClusters", StringComparison.OrdinalIgnoreCase))
            {
                return "queryClusters";
            }

            if (path.Contains("/spatialJoin", StringComparison.OrdinalIgnoreCase))
            {
                return "spatialJoin";
            }

            if (path.Contains("/queryBufferAggregate", StringComparison.OrdinalIgnoreCase))
            {
                return "queryBufferAggregate";
            }

            if (path.Contains("/queryDensity", StringComparison.OrdinalIgnoreCase))
            {
                return "queryDensity";
            }

            if (path.Contains("/createReplica", StringComparison.OrdinalIgnoreCase))
            {
                return "createReplica";
            }

            if (path.Contains("/extractChanges", StringComparison.OrdinalIgnoreCase))
            {
                return "extractChanges";
            }

            if (path.Contains("/synchronizeReplica", StringComparison.OrdinalIgnoreCase))
            {
                return "synchronizeReplica";
            }

            if (path.Contains("/unRegisterReplica", StringComparison.OrdinalIgnoreCase))
            {
                return "unRegisterReplica";
            }

            if (path.Contains("/replicas/", StringComparison.OrdinalIgnoreCase))
            {
                return "replicaInfo";
            }

            if (path.Contains("/replicas", StringComparison.OrdinalIgnoreCase))
            {
                return "replicas";
            }

            if (path.Contains("/attachments", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/addAttachment", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/updateAttachment", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/deleteAttachments", StringComparison.OrdinalIgnoreCase))
            {
                return "attachments";
            }

            if (path.Contains("/query", StringComparison.OrdinalIgnoreCase))
            {
                return "query";
            }

            return "metadata";
        }

        if (path.Contains("/MapServer", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/export", StringComparison.OrdinalIgnoreCase))
            {
                return "export";
            }

            if (path.Contains("/generateKml", StringComparison.OrdinalIgnoreCase))
            {
                return "generateKml";
            }

            if (path.Contains("/identify", StringComparison.OrdinalIgnoreCase))
            {
                return "identify";
            }

            if (path.Contains("/legend", StringComparison.OrdinalIgnoreCase))
            {
                return "legend";
            }

            if (path.Contains("/find", StringComparison.OrdinalIgnoreCase))
            {
                return "find";
            }

            if (path.Contains("/tile/", StringComparison.OrdinalIgnoreCase))
            {
                return "tile";
            }

            if (path.Contains("/WMTS", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMapServerOgcOperation(context, "wmts");
            }

            if (path.Contains("/WMS", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMapServerOgcOperation(context, "wms");
            }

            if (path.Contains("/query", StringComparison.OrdinalIgnoreCase))
            {
                return "query";
            }

            return "metadata";
        }

        if (path.Contains("/ImageServer", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/WCS", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveQueryRequestOperation(context, "wcs");
            }

            if (path.Contains("/exportImage", StringComparison.OrdinalIgnoreCase))
            {
                return "exportImage";
            }

            if (path.Contains("/identify", StringComparison.OrdinalIgnoreCase))
            {
                return "identify";
            }

            if (path.Contains("/tile/", StringComparison.OrdinalIgnoreCase))
            {
                return "tile";
            }

            if (path.Contains("/computeStatisticsHistograms", StringComparison.OrdinalIgnoreCase))
            {
                return "computeStatisticsHistograms";
            }

            if (path.Contains("/computeClassStatistics", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/computeClass", StringComparison.OrdinalIgnoreCase))
            {
                return "computeClassStatistics";
            }

            if (path.Contains("/legend", StringComparison.OrdinalIgnoreCase))
            {
                return "legend";
            }

            if (path.Contains("/query", StringComparison.OrdinalIgnoreCase))
            {
                return "query";
            }

            return "metadata";
        }

        if (path.Contains("/GeometryServer", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/areasAndLengths", StringComparison.OrdinalIgnoreCase))
            {
                return "areasAndLengths";
            }

            if (path.Contains("/lengths", StringComparison.OrdinalIgnoreCase))
            {
                return "lengths";
            }

            if (path.Contains("/buffer", StringComparison.OrdinalIgnoreCase))
            {
                return "buffer";
            }

            if (path.Contains("/simplify", StringComparison.OrdinalIgnoreCase))
            {
                return "simplify";
            }

            if (path.Contains("/project", StringComparison.OrdinalIgnoreCase))
            {
                return "project";
            }

            if (path.Contains("/intersect", StringComparison.OrdinalIgnoreCase))
            {
                return "intersect";
            }

            if (path.Contains("/union", StringComparison.OrdinalIgnoreCase))
            {
                return "union";
            }

            if (path.Contains("/clip", StringComparison.OrdinalIgnoreCase))
            {
                return "clip";
            }

            if (path.Contains("/difference", StringComparison.OrdinalIgnoreCase))
            {
                return "difference";
            }

            return "metadata";
        }

        if (StartsWithPathSegment(path, "/tiles"))
        {
            return "tile";
        }

        if (StartsWithPathSegment(path, "/stac"))
        {
            return ResolveStacOperation(path, method);
        }

        if (StartsWithPathSegment(path, "/odata"))
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

            if (HasQueryOption(context, "$apply"))
            {
                return "aggregate";
            }

            if (HasQueryOption(context, "$search"))
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

        if (StartsWithPathSegment(path, "/ogc/features"))
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

            if (path.Contains("/clusters", StringComparison.OrdinalIgnoreCase))
            {
                return "queryClusters";
            }

            if (path.Contains("/spatialJoin", StringComparison.OrdinalIgnoreCase))
            {
                return "spatialJoin";
            }

            if (path.Contains("/bufferAggregate", StringComparison.OrdinalIgnoreCase))
            {
                return "queryBufferAggregate";
            }

            if (path.Contains("/density", StringComparison.OrdinalIgnoreCase))
            {
                return "queryDensity";
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

        if (StartsWithPathSegment(path, "/ogc/coverages"))
        {
            if (string.Equals(path, "/ogc/coverages", StringComparison.OrdinalIgnoreCase))
            {
                return "landing";
            }

            if (path.EndsWith("/conformance", StringComparison.OrdinalIgnoreCase))
            {
                return "conformance";
            }

            if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/openapi.json", StringComparison.OrdinalIgnoreCase))
            {
                return "api";
            }

            if (path.EndsWith("/collections", StringComparison.OrdinalIgnoreCase))
            {
                return "collections";
            }

            if (path.EndsWith("/schema", StringComparison.OrdinalIgnoreCase))
            {
                return "schema";
            }

            if (path.EndsWith("/coverage", StringComparison.OrdinalIgnoreCase))
            {
                return "coverage";
            }

            return "collection";
        }

        if (StartsWithPathSegment(path, "/ogc/tiles"))
        {
            return "tiles";
        }

        if (StartsWithPathSegment(path, "/ogc/maps"))
        {
            return "map";
        }

        if (StartsWithPathSegment(path, "/ogc/processes"))
        {
            return ResolveOgcProcessesOperation(path, method);
        }

        if (path.Contains("/streaming", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/sessions", StringComparison.OrdinalIgnoreCase))
            {
                return "sessions";
            }

            return "stream";
        }

        return null;
    }

    private static string ResolveNAServerOperation(string path)
    {
        if (path.Contains("/solveClosestFacility", StringComparison.OrdinalIgnoreCase))
        {
            return "solveClosestFacility";
        }

        if (path.Contains("/solveServiceArea", StringComparison.OrdinalIgnoreCase))
        {
            return "solveServiceArea";
        }

        if (path.Contains("/solve", StringComparison.OrdinalIgnoreCase))
        {
            return "solveRoute";
        }

        var naServerIndex = path.IndexOf("/NAServer", StringComparison.OrdinalIgnoreCase);
        if (naServerIndex >= 0)
        {
            var suffix = path[(naServerIndex + "/NAServer".Length)..];
            if (!string.IsNullOrWhiteSpace(suffix) && suffix != "/")
            {
                return "layerInfo";
            }
        }

        return "serviceInfo";
    }

    private static string ResolveGpServerOperation(string path)
    {
        if (path.Contains("/jobs/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/results/", StringComparison.OrdinalIgnoreCase))
        {
            return "jobResult";
        }

        if (path.Contains("/jobs/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            return "cancelJob";
        }

        if (path.Contains("/jobs/", StringComparison.OrdinalIgnoreCase))
        {
            return "jobStatus";
        }

        if (path.Contains("/submitJob", StringComparison.OrdinalIgnoreCase))
        {
            return "submitJob";
        }

        var gpServerIndex = path.IndexOf("/GPServer", StringComparison.OrdinalIgnoreCase);
        if (gpServerIndex >= 0)
        {
            var suffix = path[(gpServerIndex + "/GPServer".Length)..];
            if (suffix.Contains("/execute", StringComparison.OrdinalIgnoreCase))
            {
                // The generic GPServer adapter only publishes async built-in tasks,
                // so /execute is not a live operation outside PrintingTools.
                return "unknown";
            }

            if (!string.IsNullOrWhiteSpace(suffix) && suffix != "/")
            {
                return "taskInfo";
            }
        }

        return "serviceInfo";
    }

    private static bool StartsWithPathSegment(string path, string prefix)
    {
        return string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
               (path.Length > prefix.Length &&
                path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                path[prefix.Length] == '/');
    }

    private static string ResolvePrintingToolsOperation(string path)
    {
        if (path.Contains("/Get Layout Templates Info Task/execute", StringComparison.OrdinalIgnoreCase))
        {
            return "getLayoutTemplatesInfo";
        }

        if (path.Contains("/jobs/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/results/", StringComparison.OrdinalIgnoreCase))
        {
            return "jobResult";
        }

        if (path.Contains("/jobs/", StringComparison.OrdinalIgnoreCase))
        {
            return "jobStatus";
        }

        if (path.Contains("/submitJob", StringComparison.OrdinalIgnoreCase))
        {
            return "submitJob";
        }

        if (path.Contains("/execute", StringComparison.OrdinalIgnoreCase))
        {
            return "execute";
        }

        return "serviceInfo";
    }

    private static string ResolveMapServerOgcOperation(HttpContext context, string prefix)
    {
        var request = context.Request.Query["REQUEST"].ToString();
        if (string.IsNullOrWhiteSpace(request))
        {
            return prefix;
        }

        return prefix + "." + request.Trim().ToLowerInvariant();
    }

    private static string ResolveStacOperation(string path, string method)
    {
        if (string.Equals(path, "/stac", StringComparison.OrdinalIgnoreCase))
        {
            return "catalog";
        }

        if (string.Equals(path, "/stac/collections", StringComparison.OrdinalIgnoreCase))
        {
            return "collections";
        }

        if (string.Equals(path, "/stac/search", StringComparison.OrdinalIgnoreCase))
        {
            return method.Equals(HttpMethods.Post, StringComparison.OrdinalIgnoreCase)
                ? "search.post"
                : "search.get";
        }

        if (path.StartsWith("/stac/collections/", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/items/", StringComparison.OrdinalIgnoreCase))
            {
                return "item";
            }

            if (path.EndsWith("/items", StringComparison.OrdinalIgnoreCase))
            {
                return "items";
            }

            return "collection";
        }

        return "stac";
    }

    private static string ResolveQueryRequestOperation(HttpContext context, string prefix)
    {
        var request = GetQueryOption(context, "request");
        if (string.IsNullOrWhiteSpace(request))
        {
            return prefix;
        }

        return prefix + "." + request.Trim().ToLowerInvariant();
    }

    private static bool HasQueryOption(HttpContext context, string key)
        => GetQueryOption(context, key) is { Length: > 0 };

    private static string GetQueryOption(HttpContext context, string key)
    {
        if (context.Request.Query.TryGetValue(key, out var value))
        {
            return value.ToString();
        }

        if (key.Length > 1 &&
            key[0] == '$' &&
            context.Request.Query.TryGetValue(key[1..], out value))
        {
            return value.ToString();
        }

        return string.Empty;
    }

    private static string ResolveOgcProcessesOperation(string path, string method)
    {
        if (string.Equals(path, "/ogc/processes", StringComparison.OrdinalIgnoreCase))
        {
            return "landing";
        }

        if (path.Contains("/execution", StringComparison.OrdinalIgnoreCase))
        {
            return "execute";
        }

        if (path.Contains("/jobs/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/results", StringComparison.OrdinalIgnoreCase))
        {
            return "jobResults";
        }

        if (path.Contains("/jobs/", StringComparison.OrdinalIgnoreCase))
        {
            return method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ? "dismiss" : "jobStatus";
        }

        if (path.EndsWith("/jobs", StringComparison.OrdinalIgnoreCase))
        {
            return "jobs";
        }

        if (path.EndsWith("/processes/processes", StringComparison.OrdinalIgnoreCase))
        {
            return "processes";
        }

        if (path.Contains("/processes/", StringComparison.OrdinalIgnoreCase))
        {
            return "process";
        }

        if (path.EndsWith("/conformance", StringComparison.OrdinalIgnoreCase))
        {
            return "conformance";
        }

        return "landing";
    }
}
