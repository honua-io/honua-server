// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Infrastructure.Transforms;

/// <summary>
/// PostGIS-backed coordinate transform service with fast in-memory path for common SRID pairs.
/// </summary>
internal sealed partial class PostGisCoordinateTransformService : ICoordinateTransformService
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostGisCoordinateTransformService> _logger;

    public PostGisCoordinateTransformService(
        IAdoNetDatabaseConnectionProvider connectionProvider,
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
        return await TransformExtentWithPostGisAsync(minX, minY, maxX, maxY, fromSrid, toSrid, selection: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken = default)
    {
        // When no explicit pipeline is selected, defer to the SRID-only behavior
        // (identity / in-memory fast paths + 2-argument ST_Transform).
        if (selection?.ProjPipeline is not { Length: > 0 })
        {
            return await TransformExtentAsync(minX, minY, maxX, maxY, fromSrid, toSrid, cancellationToken)
                .ConfigureAwait(false);
        }

        // An explicit pipeline must be honored exactly, so skip the in-memory fast paths
        // (identity is still a no-op, but a selected pipeline implies a real datum shift).
        if (IsIdentityTransform(fromSrid, toSrid))
        {
            return (minX, minY, maxX, maxY);
        }

        return await TransformExtentWithPostGisAsync(minX, minY, maxX, maxY, fromSrid, toSrid, selection, cancellationToken)
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
        return await TransformPointWithPostGisAsync(x, y, fromSrid, toSrid, selection: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<(double X, double Y)?> TransformPointAsync(
        double x, double y,
        int fromSrid, int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken = default)
    {
        // When no explicit pipeline is selected, defer to the SRID-only behavior
        // (identity / in-memory fast paths + 2-argument ST_Transform).
        if (selection?.ProjPipeline is not { Length: > 0 })
        {
            return await TransformPointAsync(x, y, fromSrid, toSrid, cancellationToken).ConfigureAwait(false);
        }

        // An explicit pipeline must be honored exactly, so skip in-memory fast paths
        // (identity is still a no-op, but a selected pipeline implies a real datum shift).
        if (IsIdentityTransform(fromSrid, toSrid))
        {
            return (x, y);
        }

        return await TransformPointWithPostGisAsync(x, y, fromSrid, toSrid, selection, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TransformPointsAsync(
        double[] xs,
        double[] ys,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);
        if (xs.Length != ys.Length)
        {
            throw new ArgumentException("Coordinate arrays must have the same length.", nameof(ys));
        }

        // Fast path: identity or Web Mercator alias — nothing to rewrite.
        if (xs.Length == 0 || IsIdentityTransform(fromSrid, toSrid))
        {
            return true;
        }

        // Fast path: in-memory WGS84 ↔ Web Mercator — CPU-only math, so run the whole
        // batch synchronously without per-point async overhead.
        if (IsWgs84Srid(fromSrid) && IsWebMercatorSrid(toSrid))
        {
            for (var index = 0; index < xs.Length; index++)
            {
                (xs[index], ys[index]) = LonLatToWebMercator(xs[index], ys[index]);
            }

            return true;
        }

        if (IsWebMercatorSrid(fromSrid) && IsWgs84Srid(toSrid))
        {
            for (var index = 0; index < xs.Length; index++)
            {
                (xs[index], ys[index]) = WebMercatorToLonLat(xs[index], ys[index]);
            }

            return true;
        }

        // Slow path: one PostGIS ST_Transform round trip for the entire batch.
        return await TransformPointsWithPostGisAsync(xs, ys, fromSrid, toSrid, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsIdentityTransform(int fromSrid, int toSrid)
        => fromSrid == toSrid || (IsWebMercatorSrid(fromSrid) && IsWebMercatorSrid(toSrid));

    private static bool IsWebMercatorSrid(int srid)
        => srid is 3857 or 900913 or 102100 or 102113 or 3785;

    private static bool IsWgs84Srid(int srid)
        => srid == 4326;

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
        => WebMercatorMath.LonLatToWebMercator(longitude, latitude);

    private static (double Lon, double Lat) WebMercatorToLonLat(double x, double y)
        => WebMercatorMath.WebMercatorToLonLat(x, y);

    private static (double MinX, double MinY, double MaxX, double MaxY) TransformSampledExtent(
        double minX, double minY, double maxX, double maxY,
        Func<double, double, (double X, double Y)> transform)
    {
        return WebMercatorMath.TransformSampledExtent(
            minX,
            minY,
            maxX,
            maxY,
            transform,
            ExtentSampleSegmentsPerEdge);
    }

    private async Task<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentWithPostGisAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken)
    {
        try
        {
            Log.PostGisFallbackExtent(_logger, fromSrid, toSrid);

            // Honor an explicit datum-transformation pipeline via the 3-argument
            // ST_Transform overload; otherwise let PROJ pick its default pipeline.
            string TransformOf(string pointExpression) => selection?.ProjPipeline is { Length: > 0 }
                ? $"ST_Transform({pointExpression}, @pipeline, @toSrid)"
                : $"ST_Transform({pointExpression}, @toSrid)";

            var transformExpression = TransformOf("geom");

            // A dateline-crossing geographic input (minX > maxX) must stay wrapped in the output
            // instead of collapsing the sampled X values into a single global min/max — that both
            // inflates the bbox and drops the far-side sliver. When wrapped, take the output X
            // bounds from the transformed western/eastern edges directly (mirrors the in-memory
            // WebMercatorMath.TransformSampledExtent), keeping MinX > MaxX (#2739).
            var wrapped = WebMercatorMath.IsAntimeridianCrossing(minX, maxX);
            var edgesCte = wrapped
                ? $$"""
                    ,
                    edges AS (
                        SELECT {{TransformOf("ST_SetSRID(ST_MakePoint(@minX, @minY), @fromSrid)")}} AS min_edge,
                               {{TransformOf("ST_SetSRID(ST_MakePoint(@maxX, @minY), @fromSrid)")}} AS max_edge
                    )
                    """
                : string.Empty;
            var xminSelect = wrapped ? "(SELECT ST_X(min_edge) FROM edges)" : "MIN(ST_X(geom))";
            var xmaxSelect = wrapped ? "(SELECT ST_X(max_edge) FROM edges)" : "MAX(ST_X(geom))";

            await using var connection = await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $$"""
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
                    SELECT {{transformExpression}} AS geom
                    FROM points
                ){{edgesCte}}
                SELECT {{xminSelect}} AS xmin,
                       MIN(ST_Y(geom)) AS ymin,
                       {{xmaxSelect}} AS xmax,
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
            if (selection?.ProjPipeline is { Length: > 0 } pipeline)
            {
                AddParameter(command, "@pipeline", pipeline);
            }

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
        catch (OperationCanceledException)
        {
            throw;
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
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken)
    {
        try
        {
            Log.PostGisFallbackPoint(_logger, fromSrid, toSrid);

            await using var connection = await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            // Honor an explicit datum-transformation pipeline via the 3-argument
            // ST_Transform overload; otherwise let PROJ pick its default pipeline.
            var transformExpression = selection?.ProjPipeline is { Length: > 0 }
                ? "ST_Transform(ST_SetSRID(ST_MakePoint(@x, @y), @fromSrid), @pipeline, @toSrid)"
                : "ST_Transform(ST_SetSRID(ST_MakePoint(@x, @y), @fromSrid), @toSrid)";

            command.CommandText = $"""
                SELECT ST_X(geom) AS x, ST_Y(geom) AS y
                FROM (
                    SELECT {transformExpression} AS geom
                ) t
                """;

            AddParameter(command, "@x", x);
            AddParameter(command, "@y", y);
            AddParameter(command, "@fromSrid", fromSrid);
            AddParameter(command, "@toSrid", toSrid);
            if (selection?.ProjPipeline is { Length: > 0 } pipeline)
            {
                AddParameter(command, "@pipeline", pipeline);
            }

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.PostGisTransformFailed(_logger, fromSrid, toSrid, ex);
            return null;
        }
    }

    private async Task<bool> TransformPointsWithPostGisAsync(
        double[] xs,
        double[] ys,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        try
        {
            Log.PostGisFallbackPoints(_logger, xs.Length, fromSrid, toSrid);

            await using var connection = await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ST_X(t.geom) AS x, ST_Y(t.geom) AS y
                FROM (
                    SELECT ST_Transform(ST_SetSRID(ST_MakePoint(p.x, p.y), @fromSrid), @toSrid) AS geom,
                           p.ord
                    FROM unnest(@xs, @ys) WITH ORDINALITY AS p(x, y, ord)
                ) t
                ORDER BY t.ord
                """;

            AddParameter(command, "@xs", xs);
            AddParameter(command, "@ys", ys);
            AddParameter(command, "@fromSrid", fromSrid);
            AddParameter(command, "@toSrid", toSrid);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var index = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (index >= xs.Length || reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    return false;
                }

                xs[index] = reader.GetDouble(0);
                ys[index] = reader.GetDouble(1);
                index++;
            }

            return index == xs.Length;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.PostGisTransformFailed(_logger, fromSrid, toSrid, ex);
            return false;
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

        [LoggerMessage(
            EventId = 7303,
            Level = LogLevel.Debug,
            Message = "Falling back to PostGIS ST_Transform for {PointCount} points: SRID {FromSrid} → {ToSrid}")]
        public static partial void PostGisFallbackPoints(ILogger logger, int pointCount, int fromSrid, int toSrid);
    }
}
