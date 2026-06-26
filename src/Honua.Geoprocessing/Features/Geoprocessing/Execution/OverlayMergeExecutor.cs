// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>overlay.merge</c> executor (#2139). Combines the <c>input</c> and <c>merge</c>
/// FeatureCollections into a single NEW output, concatenating features and carrying
/// each feature's own attributes through (the union-schema behaviour of Esri's
/// <c>Merge</c>; absent fields are simply not present on a given feature). Both
/// inputs are supplied inline as <c>data:application/geo+json;base64</c> data URIs.
/// Use <see cref="DataManagementAppendExecutor"/> when appending INTO an existing
/// target schema rather than producing a new merged output. Pure managed
/// NetTopologySuite — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class OverlayMergeExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
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
