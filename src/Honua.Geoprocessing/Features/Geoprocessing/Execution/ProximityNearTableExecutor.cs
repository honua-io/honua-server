// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>proximity.near-table</c> executor (#2139). Produces a TABLE of nearest-feature
/// rows — one null-geometry row per input feature that has a neighbour within the
/// optional <c>searchRadius</c> — carrying <c>IN_FID</c>, <c>NEAR_FID</c> and
/// <c>NEAR_DIST</c>, matching Esri's <c>GenerateNearTable</c>. Table rows are
/// emitted as null-geometry features so they flow through the shared
/// FeatureCollection artifact envelope. Distances are planar in CRS units. Both
/// layers are supplied inline as <c>data:application/geo+json;base64</c> data URIs.
/// Pure managed NetTopologySuite — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class ProximityNearTableExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "proximity.near-table";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var nearLayer = OverlayExecutorSupport.ReadLayer(inputs, "near", _options.CurrentValue.MaxArtifactBytes);
        var nearIdField = inputs.GetOrDefault("nearIdField", string.Empty);
        var inputIdField = inputs.GetOrDefault("inputIdField", string.Empty);
        var searchRadius = ProximityNearSupport.ReadSearchRadius(inputs);
        var nearIds = ProximityNearSupport.ResolveIds(nearLayer, string.IsNullOrWhiteSpace(nearIdField) ? null : nearIdField);

        var output = new List<IFeature>();
        for (var i = 0; i < source.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = source[i];
            var result = ProximityNearSupport.FindNearest(feature.Geometry, nearLayer, nearIds, searchRadius);
            if (!result.Found)
            {
                continue; // GenerateNearTable omits inputs with no neighbour in range.
            }

            var inFid = ProximityNearSupport.ResolveId(feature, string.IsNullOrWhiteSpace(inputIdField) ? null : inputIdField, i);
            output.Add(OverlayExecutorSupport.TableRow(new (string, object?)[]
            {
                ("IN_FID", inFid),
                ("NEAR_FID", result.NearFid),
                ("NEAR_DIST", result.NearDist),
            }));
        }

        return output;
    }
}
