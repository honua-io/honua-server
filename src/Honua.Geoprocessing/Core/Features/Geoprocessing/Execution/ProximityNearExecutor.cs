// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class ProximityNearExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "proximity.near";

    protected override string ProcessId => HandledProcessId;

    protected override async Task<List<IFeature>> ApplyAsync(
        FeatureCollection source,
        StepInputReader inputs,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        var nearLayer = await OverlayExecutorSupport.ReadLayerAsync(
            inputs, "near", opts.MaxArtifactBytes, opts.OutputRootDirectory, context, cancellationToken).ConfigureAwait(false);
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
