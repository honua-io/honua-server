// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Infrastructure.Transforms;

/// <summary>
/// PostGIS-backed coordinate transform service with fast in-memory path for common SRID pairs.
/// </summary>
internal sealed partial class PostGisCoordinateTransformService : ICoordinateTransformService
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostGisCoordinateTransformService> _logger;

    public PostGisCoordinateTransformService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostGisCoordinateTransformService> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default)
    {
        // Fast path: identity or Web Mercator alias
        if (IsIdentityTransform(fromSrid, toSrid))
        {
            return (minX, minY, maxX, maxY);
        }

        // Fast path: in-memory WGS84 ↔ Web Mercator
        if (TryTransformExtentInMemory(minX, minY, maxX, maxY, fromSrid, toSrid, out var result))
        {
            return result;
        }

        // Slow path: PostGIS ST_Transform
        return await TransformExtentWithPostGisAsync(minX, minY, maxX, maxY, fromSrid, toSrid, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<(double X, double Y)?> TransformPointAsync(
        double x, double y,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default)
    {
        // Fast path: identity or Web Mercator alias
        if (IsIdentityTransform(fromSrid, toSrid))
        {
            return (x, y);
        }

        // Fast path: in-memory WGS84 ↔ Web Mercator
        if (TryTransformPointInMemory(x, y, fromSrid, toSrid, out var result))
        {
            return result;
        }

        // Slow path: PostGIS ST_Transform
        return await TransformPointWithPostGisAsync(x, y, fromSrid, toSrid, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsIdentityTransform(int fromSrid, int toSrid)
        => fromSrid == toSrid || (IsWebMercatorSrid(fromSrid) && IsWebMercatorSrid(toSrid));

    private static bool IsWebMercatorSrid(int srid)
        => srid is 3857 or 900913 or 102100 or 102113 or 3785;

    private static bool IsWgs84Srid(int srid)
        => srid == 4326;

    private const double EarthRadius = SpatialConstants.EarthRadius;
    private const double MaxLatitude = SpatialConstants.WebMercatorMaxLatitude;
    private const int ExtentSampleSegmentsPerEdge = 4;

    private static bool TryTransformExtentInMemory(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        out (double MinX, double MinY, double MaxX, double MaxY) result)
    {
        if (IsWgs84Srid(fromSrid) && IsWebMercatorSrid(toSrid))
        {
            result = TransformSampledExtent(minX, minY, maxX, maxY, LonLatToWebMercator);
            return true;
        }

        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            result = TransformSampledExtent(minX, minY, maxX, maxY, WebMercatorToLonLat);
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
            result = LonLatToWebMercator(x, y);
            return true;
        }

        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            result = WebMercatorToLonLat(x, y);
            return true;
        }

        result = default;
        return false;
    }

    private static (double X, double Y) LonLatToWebMercator(double longitude, double latitude)
    {
        var clampedLat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        var x = longitude * Math.PI / 180.0 * EarthRadius;
        var y = Math.Log(Math.Tan((90.0 + clampedLat) * Math.PI / 360.0)) * EarthRadius;
        return (x, y);
    }

    private static (double Lon, double Lat) WebMercatorToLonLat(double x, double y)
    {
        var lon = x / EarthRadius * 180.0 / Math.PI;
        var lat = Math.Atan(Math.Exp(y / EarthRadius)) * 360.0 / Math.PI - 90.0;
        return (lon, lat);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) TransformSampledExtent(
        double minX, double minY, double maxX, double maxY,
        Func<double, double, (double X, double Y)> transform)
    {
        var transformedMinX = double.PositiveInfinity;
        var transformedMinY = double.PositiveInfinity;
        var transformedMaxX = double.NegativeInfinity;
        var transformedMaxY = double.NegativeInfinity;

        foreach (var (x, y) in EnumerateSampledExtentPoints(minX, minY, maxX, maxY))
        {
            var (tx, ty) = transform(x, y);
            transformedMinX = Math.Min(transformedMinX, tx);
            transformedMinY = Math.Min(transformedMinY, ty);
            transformedMaxX = Math.Max(transformedMaxX, tx);
            transformedMaxY = Math.Max(transformedMaxY, ty);
        }

        return (transformedMinX, transformedMinY, transformedMaxX, transformedMaxY);
    }

    private static IEnumerable<(double X, double Y)> EnumerateSampledExtentPoints(
        double minX, double minY, double maxX, double maxY)
    {
        for (var i = 0; i <= ExtentSampleSegmentsPerEdge; i++)
        {
            var t = (double)i / ExtentSampleSegmentsPerEdge;
            var x = InterpolateLongitude(minX, maxX, t);
            var y = minY + ((maxY - minY) * t);

            yield return (x, minY);
            yield return (x, maxY);
            yield return (minX, y);
            yield return (maxX, y);
        }

        yield return (InterpolateLongitude(minX, maxX, 0.5), (minY + maxY) / 2.0);
    }

    private static double InterpolateLongitude(double minX, double maxX, double t)
    {
        if (!IsAntimeridianCrossing(minX, maxX))
        {
            return minX + ((maxX - minX) * t);
        }

        var wrappedMaxX = maxX + 360.0;
        var value = minX + ((wrappedMaxX - minX) * t);
        return value > 180.0 ? value - 360.0 : value;
    }

    private static bool IsAntimeridianCrossing(double minX, double maxX)
        => minX > maxX && minX >= -180.0 && minX <= 180.0 && maxX >= -180.0 && maxX <= 180.0;

    private static string BuildLongitudeSampleExpression(string minX, string maxX, string t)
    {
        return $$"""
            CASE
                WHEN {{maxX}} >= {{minX}} THEN {{minX}} + (({{maxX}} - {{minX}}) * {{t}})
                ELSE
                    CASE
                        WHEN {{minX}} + ((({{maxX}} + 360.0) - {{minX}}) * {{t}}) > 180.0
                            THEN {{minX}} + ((({{maxX}} + 360.0) - {{minX}}) * {{t}}) - 360.0
                        ELSE {{minX}} + ((({{maxX}} + 360.0) - {{minX}}) * {{t}})
                    END
            END
            """;
    }

    private async Task<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentWithPostGisAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken)
    {
        try
        {
            Log.PostGisFallbackExtent(_logger, fromSrid, toSrid);

            await using var connection = await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH fractions AS (
                    SELECT generate_series(0, @sampleSegments)::double precision / @sampleSegments AS t
                ),
                longitude_samples AS (
                    SELECT
                        CASE
                            WHEN @maxX >= @minX THEN @minX + ((@maxX - @minX) * t)
                            ELSE
                                CASE
                                    WHEN @minX + (((@maxX + 360.0) - @minX) * t) > 180.0
                                        THEN @minX + (((@maxX + 360.0) - @minX) * t) - 360.0
                                    ELSE @minX + (((@maxX + 360.0) - @minX) * t)
                                END
                        END AS x,
                        t
                    FROM fractions
                ),
                points AS (
                    SELECT ST_SetSRID(ST_MakePoint(x, @minY), @fromSrid) AS geom
                    FROM longitude_samples
                    UNION
                    SELECT ST_SetSRID(ST_MakePoint(x, @maxY), @fromSrid) AS geom
                    FROM longitude_samples
                    UNION
                    SELECT ST_SetSRID(ST_MakePoint(@minX, @minY + ((@maxY - @minY) * t)), @fromSrid) AS geom
                    FROM fractions
                    UNION
                    SELECT ST_SetSRID(ST_MakePoint(@maxX, @minY + ((@maxY - @minY) * t)), @fromSrid) AS geom
                    FROM fractions
                    UNION
                    SELECT ST_SetSRID(
                        ST_MakePoint(
                            CASE
                                WHEN @maxX >= @minX THEN (@minX + @maxX) / 2.0
                                ELSE
                                    CASE
                                        WHEN @minX + (((@maxX + 360.0) - @minX) * 0.5) > 180.0
                                            THEN @minX + (((@maxX + 360.0) - @minX) * 0.5) - 360.0
                                        ELSE @minX + (((@maxX + 360.0) - @minX) * 0.5)
                                    END
                            END,
                            (@minY + @maxY) / 2.0
                        ),
                        @fromSrid) AS geom
                ),
                transformed AS (
                    SELECT ST_Transform(geom, @toSrid) AS geom
                    FROM points
                )
                SELECT MIN(ST_X(geom)) AS xmin,
                       MIN(ST_Y(geom)) AS ymin,
                       MAX(ST_X(geom)) AS xmax,
                       MAX(ST_Y(geom)) AS ymax
                FROM transformed
                """;

            AddParameter(command, "@minX", minX);
            AddParameter(command, "@minY", minY);
            AddParameter(command, "@maxX", maxX);
            AddParameter(command, "@maxY", maxY);
            AddParameter(command, "@fromSrid", fromSrid);
            AddParameter(command, "@toSrid", toSrid);
            AddParameter(command, "@sampleSegments", ExtentSampleSegmentsPerEdge);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                return null;
            }

            return (reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        }
        catch (Exception ex)
        {
            Log.PostGisTransformFailed(_logger, fromSrid, toSrid, ex);
            return null;
        }
    }

    private async Task<(double X, double Y)?> TransformPointWithPostGisAsync(
        double x, double y,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken)
    {
        try
        {
            Log.PostGisFallbackPoint(_logger, fromSrid, toSrid);

            await using var connection = await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ST_X(geom) AS x, ST_Y(geom) AS y
                FROM (
                    SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@x, @y), @fromSrid), @toSrid) AS geom
                ) t
                """;

            AddParameter(command, "@x", x);
            AddParameter(command, "@y", y);
            AddParameter(command, "@fromSrid", fromSrid);
            AddParameter(command, "@toSrid", toSrid);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                return null;
            }

            return (reader.GetDouble(0), reader.GetDouble(1));
        }
        catch (Exception ex)
        {
            Log.PostGisTransformFailed(_logger, fromSrid, toSrid, ex);
            return null;
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7300,
            Level = LogLevel.Debug,
            Message = "Falling back to PostGIS ST_Transform for extent: SRID {FromSrid} → {ToSrid}")]
        public static partial void PostGisFallbackExtent(ILogger logger, int fromSrid, int toSrid);

        [LoggerMessage(
            EventId = 7301,
            Level = LogLevel.Debug,
            Message = "Falling back to PostGIS ST_Transform for point: SRID {FromSrid} → {ToSrid}")]
        public static partial void PostGisFallbackPoint(ILogger logger, int fromSrid, int toSrid);

        [LoggerMessage(
            EventId = 7302,
            Level = LogLevel.Warning,
            Message = "PostGIS coordinate transform failed from SRID {FromSrid} to {ToSrid}")]
        public static partial void PostGisTransformFailed(ILogger logger, int fromSrid, int toSrid, Exception exception);
    }
}
