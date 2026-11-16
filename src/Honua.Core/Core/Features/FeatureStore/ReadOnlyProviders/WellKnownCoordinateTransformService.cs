// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class WellKnownCoordinateTransformService : ICoordinateTransformService
{
    // Mirrors PostGisCoordinateTransformService.ExtentSampleSegmentsPerEdge so the sampled
    // antimeridian-aware extent transform behaves identically.
    private const int ExtentSampleSegmentsPerEdge = 4;

    public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default)
    {
        if (IsIdentityTransform(fromSrid, toSrid))
        {
            return ValueTask.FromResult<(double, double, double, double)?>((minX, minY, maxX, maxY));
        }

        if (TryTransformExtentInMemory(minX, minY, maxX, maxY, fromSrid, toSrid, out var result))
        {
            return ValueTask.FromResult<(double, double, double, double)?>(result);
        }

        return ValueTask.FromResult<(double, double, double, double)?>(null);
    }

    public ValueTask<(double X, double Y)?> TransformPointAsync(
        double x, double y,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default)
    {
        if (IsIdentityTransform(fromSrid, toSrid))
        {
            return ValueTask.FromResult<(double, double)?>((x, y));
        }

        if (TryTransformPointInMemory(x, y, fromSrid, toSrid, out var result))
        {
            return ValueTask.FromResult<(double, double)?>(result);
        }

        return ValueTask.FromResult<(double, double)?>(null);
    }

    private static bool IsIdentityTransform(int fromSrid, int toSrid)
        => fromSrid == toSrid || (IsWebMercatorSrid(fromSrid) && IsWebMercatorSrid(toSrid));

    private static bool TryTransformExtentInMemory(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        out (double MinX, double MinY, double MaxX, double MaxY) result)
    {
        if (IsWgs84Srid(fromSrid) && IsWebMercatorSrid(toSrid))
        {
            result = WebMercatorMath.TransformSampledExtent(
                minX, minY, maxX, maxY, WebMercatorMath.LonLatToWebMercator, ExtentSampleSegmentsPerEdge);
            return true;
        }

        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            result = WebMercatorMath.TransformSampledExtent(
                minX, minY, maxX, maxY, WebMercatorMath.WebMercatorToLonLat, ExtentSampleSegmentsPerEdge);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryTransformPointInMemory(
        double x, double y,
        int fromSrid, int toSrid,
        out (double X, double Y) result)
    {
        if (IsWgs84Srid(fromSrid) && IsWebMercatorSrid(toSrid))
        {
            result = WebMercatorMath.LonLatToWebMercator(x, y);
            return true;
        }

        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            result = WebMercatorMath.WebMercatorToLonLat(x, y);
            return true;
        }

        result = default;
        return false;
    }

    private static bool IsWgs84Srid(int srid) => srid == 4326;

    private static bool IsWebMercatorSrid(int srid) => SpatialReferenceExtensions.IsWebMercatorSrid(srid);
}
