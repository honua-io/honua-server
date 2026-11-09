// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class OverlayMergeExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "overlay.merge";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var mergeLayer = OverlayExecutorSupport.ReadLayer(inputs, "merge", _options.CurrentValue.MaxArtifactBytes);

        var output = new List<IFeature>(source.Count + mergeLayer.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(new Feature(feature.Geometry, OverlayExecutorSupport.CopyAttributes(feature)));
        }

        foreach (var feature in mergeLayer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(new Feature(feature.Geometry, OverlayExecutorSupport.CopyAttributes(feature)));
        }

        return output;
    }
}
