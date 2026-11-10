// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class ProximityNearTableExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "proximity.near-table";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var nearLayer = OverlayExecutorSupport.ReadLayer(inputs, "near", _options.CurrentValue.MaxArtifactBytes);
        var nearIdField = inputs.GetOrDefault("nearIdField", string.Empty);
        var inputIdField = inputs.GetOrDefault("inputIdField", string.Empty);
        var searchRadius = ProximityNearSupport.ReadSearchRadius(inputs);
        var nearIds = ProximityNearSupport.ResolveIds(nearLayer, string.IsNullOrWhiteSpace(nearIdField) ? null : nearIdField);

        var output = new List<IFeature>();
        for (var i = 0; i < source.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = source[i];
            var result = ProximityNearSupport.FindNearest(feature.Geometry, nearLayer, nearIds, searchRadius);
            if (!result.Found)
            {
                continue; // GenerateNearTable omits inputs with no neighbour in range.
            }

            var inFid = ProximityNearSupport.ResolveId(feature, string.IsNullOrWhiteSpace(inputIdField) ? null : inputIdField, i);
            output.Add(OverlayExecutorSupport.TableRow(new (string, object?)[]
            {
                ("IN_FID", inFid),
                ("NEAR_FID", result.NearFid),
                ("NEAR_DIST", result.NearDist),
            }));
        }

        return output;
    }
}
