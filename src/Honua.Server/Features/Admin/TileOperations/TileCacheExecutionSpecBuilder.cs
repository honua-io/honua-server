// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Translates a <see cref="TileOperationStartRequest"/> into a durable
/// <see cref="ExecutionJobSpec"/> of kind <see cref="ExecutionJobKind.TileCache"/>
/// and back, keeping the encode/decode contract in one place so the admin
/// submission path and the worker-side <see cref="TileCacheJobExecutor"/> stay in
/// agreement. Pure and allocation-light; no infrastructure dependencies so it is
/// directly unit-testable.
/// </summary>
internal static class TileCacheExecutionSpecBuilder
{
    /// <summary>
    /// Builds the execution-job spec for <paramref name="request"/>, encoding every
    /// behavior-changing tile parameter onto <see cref="ExecutionJobSpec.Parameters"/>.
    /// </summary>
    public static ExecutionJobSpec Build(
        TileOperationStartRequest request,
        string? schemaName,
        TileCacheBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

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

        if (!string.IsNullOrWhiteSpace(request.TileMatrixSetId))
        {
            parameters[TileCacheJobParameterKeys.TileMatrixSetId] = request.TileMatrixSetId;
        }

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

        if (!string.IsNullOrWhiteSpace(request.GenerationId))
        {
            parameters[TileCacheJobParameterKeys.GenerationId] = request.GenerationId;
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            parameters[TileCacheJobParameterKeys.SchemaName] = schemaName;
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

    /// <summary>
    /// Reconstructs a <see cref="TileOperationStartRequest"/> from a tile-cache
    /// execution job's spec parameters. Returns <c>false</c> when the required
    /// operation key is absent or malformed so the worker can fail the job with a
    /// clean classification instead of throwing.
    /// </summary>
    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out TileOperationStartRequest request,
        out string? schemaName,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        request = null!;
        schemaName = null;
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
        parameters.TryGetValue(TileCacheJobParameterKeys.GenerationId, out var generationId);
        if (parameters.TryGetValue(TileCacheJobParameterKeys.SchemaName, out var schema)
            && !string.IsNullOrWhiteSpace(schema))
        {
            schemaName = schema;
        }

        request = new TileOperationStartRequest
        {
            Operation = operation,
            ServiceId = string.IsNullOrWhiteSpace(serviceId) ? null : serviceId,
            LayerId = layerId,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            TileMatrixSetId = string.IsNullOrWhiteSpace(tileMatrixSetId) ? null : tileMatrixSetId,
            Bbox = bbox,
            MaxTiles = maxTiles,
            GenerationId = string.IsNullOrWhiteSpace(generationId) ? null : generationId,
        };

        return true;
    }
}
