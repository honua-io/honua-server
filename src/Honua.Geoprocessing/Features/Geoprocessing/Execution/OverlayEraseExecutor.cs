// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>overlay.erase</c> executor (#2139, #2206). Layer-aware erase: subtracts the
/// union of the <c>erase</c> layer from every <c>input</c> feature's geometry,
/// keeping the input attributes verbatim. This is the layer-level Erase the
/// vector overlay/proximity pack requires and the parity counterpart to Esri's
/// <c>Erase_analysis</c>. Input features fully covered by the erase layer are
/// dropped. Both layers are supplied inline as
/// <c>data:application/geo+json;base64</c> data URIs. Pure managed
/// NetTopologySuite overlay — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class OverlayEraseExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
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
