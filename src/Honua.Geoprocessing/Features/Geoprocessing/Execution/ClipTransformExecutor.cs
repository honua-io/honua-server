// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.clip</c> executor. Clips each feature's geometry to an
/// area-of-interest region (the geometric intersection), dropping features that
/// fall entirely outside the region. Pure managed NetTopologySuite overlay — no
/// GEOS native dependency. A feature whose clipped geometry is empty is dropped;
/// the clipped geometry keeps the source feature's SRID and attributes are
/// preserved. Ported from the GeoETL baseline ClipTransform onto the #1185
/// process/executor contract. Streams: a per-feature map; the region is parsed once
/// before the stream is consumed.
/// </summary>
internal sealed class ClipTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.clip";

    protected override string ProcessId => HandledProcessId;

    protected override async IAsyncEnumerable<IFeature> ApplyStream(
        IAsyncEnumerable<IFeature> source,
        StepInputReader inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var region = SpatialFilterTransformExecutor.ReadRegion(inputs);

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clipped = ClipFeature(feature, region);
            if (clipped is not null)
            {
                yield return clipped;
            }
        }
    }

    private static Feature? ClipFeature(IFeature feature, NtsGeometry region)
    {
        var geometry = feature.Geometry;
        if (geometry is null || geometry.IsEmpty)
        {
            return null;
        }

        // Cheap envelope reject before the overlay for features clearly outside the region.
        if (!geometry.EnvelopeInternal.Intersects(region.EnvelopeInternal))
        {
            return null;
        }

        NtsGeometry clipped;
        try
        {
            clipped = geometry.Intersection(region);
        }
        catch (TopologyException)
        {
            // Row-level geometry error: drop the row rather than aborting the run.
            return null;
        }

        if (clipped.IsEmpty)
        {
            return null;
        }

        clipped.SRID = geometry.SRID;
        return new Feature(clipped, feature.Attributes);
    }
}
