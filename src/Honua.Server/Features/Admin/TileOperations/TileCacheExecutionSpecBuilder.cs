// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.TileOperations;

internal static class TileCacheExecutionSpecBuilder
{
    private const string DefaultTileMatrixSetId = "WebMercatorQuad";

    public static ExecutionJobSpec Build(
        TileOperationStartRequest request,
        string? schemaName,
        TileCacheBatchOptions options)
        => Build(request, schemaName, tenantScope: null, options);

    public static ExecutionJobSpec Build(
        TileOperationStartRequest request,
        string? schemaName,
        string? tenantScope,
        TileCacheBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        request = NormalizeRequest(request);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TileCacheJobParameterKeys.Operation] = request.Operation,
        };

        if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            parameters[TileCacheJobParameterKeys.ServiceId] = request.ServiceId;
        }

        if (request.LayerId.HasValue)
        {
            parameters[TileCacheJobParameterKeys.LayerId] =
                request.LayerId.Value.ToString(CultureInfo.InvariantCulture);
        }

        parameters[TileCacheJobParameterKeys.TileMatrixSetId] = string.IsNullOrWhiteSpace(request.TileMatrixSetId)
            ? DefaultTileMatrixSetId
            : request.TileMatrixSetId;

        if (request.MinZoom.HasValue)
        {
            parameters[TileCacheJobParameterKeys.MinZoom] =
                request.MinZoom.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (request.MaxZoom.HasValue)
        {
            parameters[TileCacheJobParameterKeys.MaxZoom] =
                request.MaxZoom.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (request.Bbox is { Length: 4 })
        {
            parameters[TileCacheJobParameterKeys.Bbox] = string.Join(
                ',',
                request.Bbox.Select(static v => v.ToString("R", CultureInfo.InvariantCulture)));
        }

        if (request.MaxTiles.HasValue)
        {
            parameters[TileCacheJobParameterKeys.MaxTiles] =
                request.MaxTiles.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(request.Style))
        {
            parameters[TileCacheJobParameterKeys.Style] = request.Style;
        }

        if (!string.IsNullOrWhiteSpace(request.Format))
        {
            parameters[TileCacheJobParameterKeys.Format] = request.Format;
        }

        if (!string.IsNullOrWhiteSpace(request.GenerationId))
        {
            parameters[TileCacheJobParameterKeys.GenerationId] = request.GenerationId;
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            parameters[TileCacheJobParameterKeys.SchemaName] = schemaName;
        }

        if (!string.IsNullOrWhiteSpace(tenantScope))
        {
            parameters[TileCacheJobParameterKeys.TenantScope] = tenantScope;
        }

        // Backend-specific coordinates (AWS Batch job-definition/queue ARNs,
        // Kubernetes namespace/image overrides, target artifact bucket) are merged
        // last so an operator can pin them per deployment without changing code.
        foreach (var kv in options.Parameters)
        {
            parameters[kv.Key] = kv.Value;
        }

        var serviceSegment = string.IsNullOrWhiteSpace(request.ServiceId) ? "_" : request.ServiceId;
        var layerSegment = request.LayerId?.ToString(CultureInfo.InvariantCulture) ?? "_";

        return new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.TileCache,
            TargetKind = options.TargetKind,
            Backend = options.Backend,
            WorkloadName = $"tilecache:{request.Operation}:{serviceSegment}:{layerSegment}",
            Artifact = options.Artifact,
            RuntimeProfile = options.RuntimeProfile,
            Parameters = parameters,
        };
    }

    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out TileOperationStartRequest request,
        out string? schemaName,
        out string error)
        => TryParse(parameters, out request, out schemaName, out _, out error);

    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out TileOperationStartRequest request,
        out string? schemaName,
        out string? tenantScope,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        request = null!;
        schemaName = null;
        tenantScope = null;
        error = string.Empty;

        if (!parameters.TryGetValue(TileCacheJobParameterKeys.Operation, out var operation)
            || string.IsNullOrWhiteSpace(operation))
        {
            error = "missing required tile-cache parameter 'operation'";
            return false;
        }

        int? layerId = null;
        if (parameters.TryGetValue(TileCacheJobParameterKeys.LayerId, out var layerRaw)
            && !string.IsNullOrWhiteSpace(layerRaw))
        {
            if (!int.TryParse(layerRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLayer))
            {
                error = "invalid tile-cache parameter 'layer_id'";
                return false;
            }

            layerId = parsedLayer;
        }

        int? minZoom = null;
        if (parameters.TryGetValue(TileCacheJobParameterKeys.MinZoom, out var minRaw)
            && !string.IsNullOrWhiteSpace(minRaw))
        {
            if (!int.TryParse(minRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMin))
            {
                error = "invalid tile-cache parameter 'min_zoom'";
                return false;
            }

            minZoom = parsedMin;
        }

        int? maxZoom = null;
        if (parameters.TryGetValue(TileCacheJobParameterKeys.MaxZoom, out var maxRaw)
            && !string.IsNullOrWhiteSpace(maxRaw))
        {
            if (!int.TryParse(maxRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax))
            {
                error = "invalid tile-cache parameter 'max_zoom'";
                return false;
            }

            maxZoom = parsedMax;
        }

        int? maxTiles = null;
        if (parameters.TryGetValue(TileCacheJobParameterKeys.MaxTiles, out var maxTilesRaw)
            && !string.IsNullOrWhiteSpace(maxTilesRaw))
        {
            if (!int.TryParse(maxTilesRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxTiles))
            {
                error = "invalid tile-cache parameter 'max_tiles'";
                return false;
            }

            maxTiles = parsedMaxTiles;
        }

        double[]? bbox = null;
        if (parameters.TryGetValue(TileCacheJobParameterKeys.Bbox, out var bboxRaw)
            && !string.IsNullOrWhiteSpace(bboxRaw))
        {
            var parts = bboxRaw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                error = "invalid tile-cache parameter 'bbox'; expected four comma-separated values";
                return false;
            }

            var values = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    error = "invalid tile-cache parameter 'bbox'; values must be finite numbers";
                    return false;
                }
            }

            bbox = values;
        }

        parameters.TryGetValue(TileCacheJobParameterKeys.ServiceId, out var serviceId);
        parameters.TryGetValue(TileCacheJobParameterKeys.TileMatrixSetId, out var tileMatrixSetId);
        parameters.TryGetValue(TileCacheJobParameterKeys.Style, out var style);
        parameters.TryGetValue(TileCacheJobParameterKeys.Format, out var format);
        parameters.TryGetValue(TileCacheJobParameterKeys.GenerationId, out var generationId);
        if (parameters.TryGetValue(TileCacheJobParameterKeys.SchemaName, out var schema)
            && !string.IsNullOrWhiteSpace(schema))
        {
            schemaName = schema;
        }

        if (parameters.TryGetValue(TileCacheJobParameterKeys.TenantScope, out var scope)
            && !string.IsNullOrWhiteSpace(scope))
        {
            tenantScope = scope;
        }

        var parsedRequest = new TileOperationStartRequest
        {
            Operation = operation,
            ServiceId = string.IsNullOrWhiteSpace(serviceId) ? null : serviceId,
            LayerId = layerId,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            TileMatrixSetId = string.IsNullOrWhiteSpace(tileMatrixSetId) ? DefaultTileMatrixSetId : tileMatrixSetId,
            Bbox = bbox,
            MaxTiles = maxTiles,
            Style = string.IsNullOrWhiteSpace(style) ? null : style,
            Format = string.IsNullOrWhiteSpace(format) ? null : format,
            GenerationId = string.IsNullOrWhiteSpace(generationId) ? null : generationId,
        };

        try
        {
            request = parsedRequest with { Operation = NormalizeOperation(parsedRequest.Operation) };
        }
        catch (ArgumentException)
        {
            error = $"invalid tile-cache parameter 'operation'; got '{operation}'";
            return false;
        }

        return true;
    }

    public static TileOperationStartRequest NormalizeRequest(TileOperationStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operation = NormalizeOperation(request.Operation);

        return request with
        {
            Operation = operation,
            TileMatrixSetId = string.IsNullOrWhiteSpace(request.TileMatrixSetId)
                ? DefaultTileMatrixSetId
                : request.TileMatrixSetId.Trim(),
            Style = string.IsNullOrWhiteSpace(request.Style)
                ? "default"
                : request.Style.Trim(),
            Format = string.IsNullOrWhiteSpace(request.Format)
                ? null
                : request.Format.Trim().ToLowerInvariant(),
        };
    }

    private static string NormalizeOperation(string? operation)
    {
        var normalized = operation?.Trim().ToLowerInvariant();
        if (normalized is not ("seed" or "warm" or "invalidate" or "purge" or "archive" or "publish" or "expire" or "delete"))
        {
            throw new ArgumentException(
                "Operation must be one of: seed, warm, invalidate, purge, archive, publish, expire, delete.",
                nameof(operation));
        }

        return normalized;
    }

    public static string BuildPartitionKey(
        TileOperationStartRequest request,
        IReadOnlyList<int>? resolvedLayerIds = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetSegment = resolvedLayerIds is { Count: > 0 } ? "layers" : "unresolved";
        var gridsetSegment = string.IsNullOrWhiteSpace(request.TileMatrixSetId)
            ? DefaultTileMatrixSetId
            : GeneratedTileCacheKey.Sanitize(request.TileMatrixSetId);
        var styleSegment = GeneratedTileCacheKey.Sanitize(
            string.IsNullOrWhiteSpace(request.Style) ? "default" : request.Style);

        return $"tilecache:{targetSegment}:{gridsetSegment}:{styleSegment}";
    }
}
