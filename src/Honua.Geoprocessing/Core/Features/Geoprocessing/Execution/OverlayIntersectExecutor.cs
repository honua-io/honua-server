// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;

namespace Honua.Geoprocessing.Execution;

internal sealed class OverlayIntersectExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "overlay.intersect";

    private const string OverlayPrefix = "OVERLAY_";

    protected override string ProcessId => HandledProcessId;

    protected override async Task<List<IFeature>> ApplyAsync(
        FeatureCollection source,
        StepInputReader inputs,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        var overlayLayer = await OverlayExecutorSupport.ReadLayerAsync(
            inputs, "overlay", opts.MaxArtifactBytes, opts.OutputRootDirectory, context, cancellationToken).ConfigureAwait(false);
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
