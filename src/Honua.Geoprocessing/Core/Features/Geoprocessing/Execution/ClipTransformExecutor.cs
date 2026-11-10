// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

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
