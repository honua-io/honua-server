// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>proximity.near</c> executor (#2139). For every <c>input</c> feature, appends
/// <c>NEAR_FID</c> and <c>NEAR_DIST</c> attributes describing the closest feature
/// in the <c>near</c> layer (Esri <c>Near</c> semantics), preserving the input
/// geometry and attributes one-to-one. Distances are planar in CRS units. Input
/// features with no neighbour within the optional <c>searchRadius</c> receive
/// <c>NEAR_FID = -1</c> and <c>NEAR_DIST = -1</c>. Both layers are supplied inline
/// as <c>data:application/geo+json;base64</c> data URIs. Pure managed
/// NetTopologySuite — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class ProximityNearExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "proximity.near";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var nearLayer = OverlayExecutorSupport.ReadLayer(inputs, "near", _options.CurrentValue.MaxArtifactBytes);
        var idField = inputs.GetOrDefault("nearIdField", string.Empty);
        var searchRadius = ProximityNearSupport.ReadSearchRadius(inputs);
        var nearIds = ProximityNearSupport.ResolveIds(nearLayer, string.IsNullOrWhiteSpace(idField) ? null : idField);

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ProximityNearSupport.FindNearest(feature.Geometry, nearLayer, nearIds, searchRadius);

            var attributes = OverlayExecutorSupport.CopyAttributes(feature);
            OverlayExecutorSupport.Upsert(attributes, "NEAR_FID", result.NearFid);
            OverlayExecutorSupport.Upsert(
                attributes,
                "NEAR_DIST",
                result.Found ? result.NearDist : (double)ProximityNearSupport.NoNeighbourFid);
            output.Add(new Feature(feature.Geometry, attributes));
        }

        return output;
    }
}
