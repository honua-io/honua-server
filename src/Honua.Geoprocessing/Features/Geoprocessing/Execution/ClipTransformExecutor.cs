// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// <c>transform.clip</c> executor. Clips each feature's geometry to an
/// area-of-interest region (the geometric intersection), dropping features that
/// fall entirely outside the region. Pure managed NetTopologySuite overlay — no
/// GEOS native dependency. A feature whose clipped geometry is empty is dropped;
/// the clipped geometry keeps the source feature's SRID and attributes are
/// preserved. Ported from the GeoETL baseline ClipTransform onto the #1185
/// process/executor contract.
/// </summary>
internal sealed class ClipTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.clip";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var region = SpatialFilterTransformExecutor.ReadRegion(inputs);

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            // Cheap envelope reject before the overlay for features clearly outside the region.
            if (!geometry.EnvelopeInternal.Intersects(region.EnvelopeInternal))
            {
                continue;
            }

            NtsGeometry clipped;
            try
            {
                clipped = geometry.Intersection(region);
            }
            catch (TopologyException)
            {
                // Row-level geometry error: drop the row rather than aborting the run.
                continue;
            }

            if (clipped.IsEmpty)
            {
                continue;
            }

            clipped.SRID = geometry.SRID;
            output.Add(new Feature(clipped, feature.Attributes));
        }

        return output;
    }
}
