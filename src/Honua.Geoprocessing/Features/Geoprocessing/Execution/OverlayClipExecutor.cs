// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>overlay.clip</c> executor (#2206). Layer-aware clip: truncates every
/// <c>input</c> feature's geometry to the union of the <c>clip</c> layer, keeping
/// the input attributes verbatim. Input features that fall entirely outside the
/// clip region are dropped, matching Esri's <c>Clip_analysis</c> semantics. Both
/// layers are supplied inline as <c>data:application/geo+json;base64</c> data
/// URIs, mirroring <see cref="ManagedSpatialJoinExecutor"/> so the lean dispatcher
/// can construct it without a Postgres dependency. Pure managed NetTopologySuite
/// overlay — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class OverlayClipExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "overlay.clip";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var clipLayer = OverlayExecutorSupport.ReadLayer(inputs, "clip", _options.CurrentValue.MaxArtifactBytes);
        var clipGeometry = OverlayExecutorSupport.UnionGeometry(clipLayer);

        var output = new List<IFeature>(source.Count);
        if (clipGeometry is null)
        {
            return output; // Nothing to clip against — empty result.
        }

        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty || !geometry.Intersects(clipGeometry))
            {
                continue;
            }

            var clipped = geometry.Intersection(clipGeometry);
            if (clipped is null || clipped.IsEmpty)
            {
                continue;
            }

            output.Add(new Feature(clipped, OverlayExecutorSupport.CopyAttributes(feature)));
        }

        return output;
    }
}
