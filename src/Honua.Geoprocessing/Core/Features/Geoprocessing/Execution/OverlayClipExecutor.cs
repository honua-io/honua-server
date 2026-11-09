// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class OverlayClipExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
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
