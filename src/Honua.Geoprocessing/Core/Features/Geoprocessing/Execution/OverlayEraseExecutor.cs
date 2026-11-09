// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class OverlayEraseExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "overlay.erase";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var eraseLayer = OverlayExecutorSupport.ReadLayer(inputs, "erase", _options.CurrentValue.MaxArtifactBytes);
        var eraseGeometry = OverlayExecutorSupport.UnionGeometry(eraseLayer);

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            // No erase region (or no overlap) leaves the input geometry untouched.
            if (eraseGeometry is null || !geometry.Intersects(eraseGeometry))
            {
                output.Add(new Feature(geometry, OverlayExecutorSupport.CopyAttributes(feature)));
                continue;
            }

            var remainder = geometry.Difference(eraseGeometry);
            if (remainder is null || remainder.IsEmpty)
            {
                continue;
            }

            output.Add(new Feature(remainder, OverlayExecutorSupport.CopyAttributes(feature)));
        }

        return output;
    }
}
