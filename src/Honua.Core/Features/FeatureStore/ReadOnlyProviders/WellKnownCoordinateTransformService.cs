// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// <see cref="ICoordinateTransformService"/> implementation covering only the transform
/// paths <see cref="ICoordinateTransformService"/>'s own documentation calls out as
/// in-memory / provider-independent: identity, Web Mercator aliasing, and WGS84 (4326)
/// &#8596; Web Mercator (3857). Registered by read-only feature providers (DuckDB,
/// MySQL/MariaDB) so DI activation succeeds for consumers that require
/// <see cref="ICoordinateTransformService"/> (for example
/// <c>Honua.Protocols.Ogc.Shared.OgcFeaturesGeometryServices</c>, a mandatory dependency of
/// OGC API Features wired unconditionally regardless of provider).
/// </summary>
/// <remarks>
/// <para>
/// Found under honua-server#2947 (secondary-provider HTTP-stack GA proof): with no
/// <see cref="ICoordinateTransformService"/> registration at all, every OGC API Features
/// request failed DI activation outright under <c>DataSource:Provider=duckdb</c> or
/// <c>mysql</c> — not just requests that transform between CRSes. Only
/// <c>Honua.Postgres.Features.Infrastructure.Transforms.PostGisCoordinateTransformService</c>
/// ever registered an implementation, because arbitrary SRID-pair transforms genuinely
/// depend on PostGIS's <c>ST_Transform</c>/<c>spatial_ref_sys</c>.
/// </para>
/// <para>
/// This is not a fake/no-op: it reuses the exact same <see cref="WebMercatorMath"/> helper
/// the PostGIS implementation itself uses for its own documented in-memory fast path (see
/// <see cref="ICoordinateTransformService"/>'s remarks table), so results for the
/// identity/Web-Mercator-alias/4326&#8596;3857 cases are byte-identical to what the PostGIS
/// implementation would return without ever touching the database. Any other SRID pair
/// returns <see langword="null"/> ("cannot be performed"), the same contract the interface
/// already documents for an unknown SRID — a provider with no <c>spatial_ref_sys</c>
/// catalog genuinely cannot resolve arbitrary datum transforms, so reporting "not
/// supported" is the correct, capability-scoped answer.
/// </para>
/// </remarks>
public sealed class WellKnownCoordinateTransformService : ICoordinateTransformService
{
    // Mirrors PostGisCoordinateTransformService.ExtentSampleSegmentsPerEdge so the sampled
    // antimeridian-aware extent transform behaves identically.
    private const int ExtentSampleSegmentsPerEdge = 4;

    /// <inheritdoc />
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

    /// <inheritdoc />
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
