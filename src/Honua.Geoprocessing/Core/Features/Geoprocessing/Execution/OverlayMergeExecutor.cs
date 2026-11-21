// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class OverlayMergeExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "overlay.merge";

    protected override string ProcessId => HandledProcessId;

    protected override async Task<List<IFeature>> ApplyAsync(
        FeatureCollection source,
        StepInputReader inputs,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        var mergeLayer = await OverlayExecutorSupport.ReadLayerAsync(
            inputs, "merge", opts.MaxArtifactBytes, opts.OutputRootDirectory, context, cancellationToken).ConfigureAwait(false);

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
