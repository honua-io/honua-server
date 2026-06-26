// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>overlay.union</c> executor (#2206). Layer-aware planar union: computes the
/// full topological overlay of the <c>input</c> and <c>overlay</c> layers,
/// emitting three disjoint piece sets — input-only (input geometry minus the
/// overlay union, with input attributes), overlay-only (overlay geometry minus the
/// input union, with overlay attributes), and the pairwise intersections (with
/// input + overlay attributes merged, overlay collisions prefixed
/// <c>OVERLAY_</c>). This mirrors Esri's <c>Union_analysis</c> on two layers
/// addressed inline. Pure managed NetTopologySuite overlay — no GDAL/GEOS native
/// dependency.
/// </summary>
internal sealed class OverlayUnionExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "overlay.union";

    private const string OverlayPrefix = "OVERLAY_";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var overlayLayer = OverlayExecutorSupport.ReadLayer(inputs, "overlay", _options.CurrentValue.MaxArtifactBytes);

        var inputUnion = OverlayExecutorSupport.UnionGeometry(source);
        var overlayUnion = OverlayExecutorSupport.UnionGeometry(overlayLayer);

        var output = new List<IFeature>();

        // Input-only remainder + intersection pieces.
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            AddRemainder(output, geometry, overlayUnion, feature, overlayLayer, cancellationToken);
        }

        // Overlay-only remainder (intersection pieces already emitted above).
        foreach (var feature in overlayLayer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            var remainder = inputUnion is null ? geometry : geometry.Difference(inputUnion);
            if (remainder is not null && !remainder.IsEmpty)
            {
                output.Add(new Feature(remainder, OverlayExecutorSupport.CopyAttributes(feature)));
            }
        }

        return output;
    }

    private static void AddRemainder(
        List<IFeature> output,
        NtsGeometry geometry,
        NtsGeometry? overlayUnion,
        IFeature inputFeature,
        FeatureCollection overlayLayer,
        CancellationToken cancellationToken)
    {
        // Input-only remainder.
        var remainder = overlayUnion is null ? geometry : geometry.Difference(overlayUnion);
        if (remainder is not null && !remainder.IsEmpty)
        {
            output.Add(new Feature(remainder, OverlayExecutorSupport.CopyAttributes(inputFeature)));
        }

        if (overlayUnion is null || !geometry.Intersects(overlayUnion))
        {
            return;
        }

        // Intersection pieces, one per overlapping overlay feature, with merged attributes.
        foreach (var other in overlayLayer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var otherGeometry = other.Geometry;
            if (otherGeometry is null || otherGeometry.IsEmpty || !geometry.Intersects(otherGeometry))
            {
                continue;
            }

            var piece = geometry.Intersection(otherGeometry);
            if (piece is null || piece.IsEmpty)
            {
                continue;
            }

            var attributes = OverlayExecutorSupport.CopyAttributes(inputFeature);
            OverlayExecutorSupport.MergeAttributes(attributes, other, OverlayPrefix);
            output.Add(new Feature(piece, attributes));
        }
    }
}
