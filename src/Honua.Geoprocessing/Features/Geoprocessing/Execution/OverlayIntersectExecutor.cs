// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>overlay.intersect</c> executor (#2206). Layer-aware intersect: emits the
/// pairwise geometric intersection of every <c>input</c> feature with every
/// overlapping <c>overlay</c> feature, carrying the input attributes and merging
/// the overlay attributes (overlay keys that collide with input keys are prefixed
/// <c>OVERLAY_</c>). This is the parity counterpart to Esri's
/// <c>Intersect_analysis</c> over two layers addressed inline. An in-memory
/// <see cref="STRtree{T}"/> indexes the overlay layer so the pairing is not a full
/// cross product. Pure managed NetTopologySuite overlay — no GDAL/GEOS native
/// dependency.
/// </summary>
internal sealed class OverlayIntersectExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "overlay.intersect";

    private const string OverlayPrefix = "OVERLAY_";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var overlayLayer = OverlayExecutorSupport.ReadLayer(inputs, "overlay", _options.CurrentValue.MaxArtifactBytes);
        var index = BuildIndex(overlayLayer, cancellationToken);

        var output = new List<IFeature>();
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            foreach (var candidate in index.Query(geometry.EnvelopeInternal))
            {
                var other = candidate.Geometry;
                if (other is null || other.IsEmpty || !geometry.Intersects(other))
                {
                    continue;
                }

                var piece = geometry.Intersection(other);
                if (piece is null || piece.IsEmpty)
                {
                    continue;
                }

                var attributes = OverlayExecutorSupport.CopyAttributes(feature);
                OverlayExecutorSupport.MergeAttributes(attributes, candidate, OverlayPrefix);
                output.Add(new Feature(piece, attributes));
            }
        }

        return output;
    }

    private static STRtree<IFeature> BuildIndex(FeatureCollection overlay, CancellationToken cancellationToken)
    {
        var index = new STRtree<IFeature>();
        foreach (var feature in overlay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is not null && !geometry.IsEmpty)
            {
                index.Insert(geometry.EnvelopeInternal, feature);
            }
        }

        index.Build();
        return index;
    }
}
