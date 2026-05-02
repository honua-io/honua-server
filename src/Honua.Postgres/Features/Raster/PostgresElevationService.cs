// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

internal sealed class PostgresElevationService : IElevationService
{
    private const int DefaultPointSrid = 4326;
    private const int Wgs84Srid = 4326;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<PostgresElevationService> _logger;
    private readonly string _rasterDataTable;

    public PostgresElevationService(
        IDatabaseConnectionProvider connectionProvider,
        IRasterStore rasterStore,
        ILogger<PostgresElevationService> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
    }

    public async Task<ElevationPointResult> QueryPointAsync(
        int layerId,
        double x,
        double y,
        int? srid,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default)
    {
        var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No raster data is registered for this elevation dataset.");
        }

        var sourceMetadata = ResolveSourceMetadata(rasters);
        var resolvedSrid = srid ?? DefaultPointSrid;

        var pointWkb = BuildPointWkb(x, y);
        var matching = await _rasterStore.QueryRastersAsync(
            layerId,
            new RasterSelectionQuery
            {
                Geometry = pointWkb,
                GeometrySrid = resolvedSrid
            },
            cancellationToken).ConfigureAwait(false);

        if (matching.Length == 0)
        {
            PostgresElevationLog.ElevationPointQueried(_logger, layerId, x, y, resolvedSrid, 0, noData: true);
            return new ElevationPointResult
            {
                Elevation = null,
                NoData = true,
                OutOfBounds = true,
                LayerId = layerId,
                RasterIds = Array.Empty<long>(),
                X = x,
                Y = y,
                QuerySrid = resolvedSrid,
                SourceSrid = sourceMetadata.SourceSrid,
                PixelType = sourceMetadata.PixelType,
                NoDataValue = sourceMetadata.NoDataValue,
                VerticalUnit = sourceMetadata.VerticalUnit,
                VerticalDatum = sourceMetadata.VerticalDatum
            };
        }

        var rasterIds = new long[matching.Length];
        for (var i = 0; i < matching.Length; i++)
        {
            rasterIds[i] = matching[i].Id;
        }

        var pixel = await _rasterStore.IdentifyMosaicAsync(
            layerId,
            rasterIds,
            mergeStrategy,
            x,
            y,
            resolvedSrid,
            cancellationToken).ConfigureAwait(false);

        double? elevation = null;
        if (pixel.HasData && pixel.BandValues.TryGetValue(1, out var raw) && raw is double value)
        {
            elevation = value;
        }
        else if (pixel.BandValues.TryGetValue(1, out var rawAny) && rawAny is double anyValue)
        {
            elevation = anyValue;
        }

        var noData = !elevation.HasValue;
        PostgresElevationLog.ElevationPointQueried(_logger, layerId, x, y, resolvedSrid, rasterIds.Length, noData);

        return new ElevationPointResult
        {
            Elevation = elevation,
            NoData = noData,
            OutOfBounds = false,
            LayerId = layerId,
            RasterIds = rasterIds,
            X = x,
            Y = y,
            QuerySrid = resolvedSrid,
            SourceSrid = sourceMetadata.SourceSrid,
            PixelType = sourceMetadata.PixelType,
            NoDataValue = sourceMetadata.NoDataValue,
            VerticalUnit = sourceMetadata.VerticalUnit,
            VerticalDatum = sourceMetadata.VerticalDatum
        };
    }

    public async Task<ElevationProfileResult> QueryProfileAsync(
        int layerId,
        byte[] lineWkb,
        int lineSrid,
        ProfileSamplingOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lineWkb);

        if (options.MaxSampleCount < 2)
        {
            throw new ArgumentException("MaxSampleCount must be at least 2.", nameof(options));
        }

        if (options.SampleCount.HasValue && options.SampleCount.Value < 2)
        {
            throw new ArgumentException("SampleCount must be at least 2 when supplied.", nameof(options));
        }

        var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No raster data is registered for this elevation dataset.");
        }

        var sourceMetadata = ResolveSourceMetadata(rasters);
        var matching = await _rasterStore.QueryRastersAsync(
            layerId,
            new RasterSelectionQuery
            {
                Geometry = lineWkb,
                GeometrySrid = lineSrid
            },
            cancellationToken).ConfigureAwait(false);

        var rasterIds = new long[matching.Length];
        for (var i = 0; i < matching.Length; i++)
        {
            rasterIds[i] = matching[i].Id;
        }

        var samples = new List<ElevationSample>(Math.Min(options.MaxSampleCount, options.SampleCount ?? options.DefaultSampleCount));
        var allNoData = true;
        var lineLength = 0d;

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (rasterIds.Length == 0)
        {
            command.CommandText = $"""
                WITH line_in AS (
                    SELECT ST_GeomFromWKB(@lineWkb, @lineSrid) AS geom_in
                ),
                line_4326 AS (
                    SELECT CASE
                               WHEN @lineSrid = {Wgs84Srid} THEN geom_in
                               ELSE ST_Transform(geom_in, {Wgs84Srid})
                           END AS geom
                    FROM line_in
                ),
                line_g AS (
                    SELECT geom, geom::geography AS geog FROM line_4326
                ),
                params AS (
                    SELECT geog,
                           ST_Length(geog) AS len_m,
                           {ResolvedSampleCountExpression} AS n_samples
                    FROM line_g
                ),
                fracs AS (
                    SELECT n,
                           CASE WHEN params.n_samples > 1
                                THEN n / (params.n_samples - 1)::float
                                ELSE 0
                           END AS frac
                    FROM params CROSS JOIN generate_series(0, params.n_samples - 1) AS gs(n)
                )
                SELECT fracs.n,
                       fracs.frac * params.len_m AS dist_m,
                       params.len_m
                FROM fracs CROSS JOIN params
                ORDER BY fracs.n
                """;
            AddProfileSqlParameters(command, lineWkb, lineSrid, options);

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var dist = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                    if (!reader.IsDBNull(2))
                    {
                        lineLength = Math.Max(lineLength, reader.GetDouble(2));
                    }

                    samples.Add(new ElevationSample
                    {
                        DistanceMeters = dist,
                        Elevation = null,
                        NoData = true
                    });
                }
            }

            if (samples.Count == 0)
            {
                samples.Add(new ElevationSample { DistanceMeters = 0, Elevation = null, NoData = true });
                samples.Add(new ElevationSample { DistanceMeters = lineLength, Elevation = null, NoData = true });
            }

            PostgresElevationLog.ElevationProfileQueried(
                _logger,
                layerId,
                samples.Count,
                lineLength,
                rasterCount: 0,
                allNoData: true);

            return new ElevationProfileResult
            {
                Samples = samples.ToArray(),
                LineLengthMeters = lineLength,
                SampleCount = samples.Count,
                LayerId = layerId,
                RasterIds = rasterIds,
                SourceSrid = sourceMetadata.SourceSrid,
                PixelType = sourceMetadata.PixelType,
                NoDataValue = sourceMetadata.NoDataValue,
                VerticalUnit = sourceMetadata.VerticalUnit,
                VerticalDatum = sourceMetadata.VerticalDatum,
                IsAllNoData = true
            };
        }

        var aggregateExpression = RasterMosaicSql.CreateMosaicAggregateExpression(mergeStrategy);
        command.CommandText = $"""
            WITH line_in AS (
                SELECT ST_GeomFromWKB(@lineWkb, @lineSrid) AS geom_in
            ),
            line_4326 AS (
                SELECT CASE
                           WHEN @lineSrid = {Wgs84Srid} THEN geom_in
                           ELSE ST_Transform(geom_in, {Wgs84Srid})
                       END AS geom
                FROM line_in
            ),
            line_g AS (
                SELECT geom, geom::geography AS geog FROM line_4326
            ),
            params AS (
                SELECT geom,
                       geog,
                       ST_Length(geog) AS len_m,
                       {ResolvedSampleCountExpression} AS n_samples
                FROM line_g
            ),
            fracs AS (
                SELECT n,
                       CASE WHEN params.n_samples > 1
                            THEN n / (params.n_samples - 1)::float
                            ELSE 0
                       END AS frac
                FROM params CROSS JOIN generate_series(0, params.n_samples - 1) AS gs(n)
            ),
            pts AS (
                SELECT fracs.n AS idx,
                       fracs.frac * params.len_m AS dist_m,
                       params.len_m AS line_length_m,
                       ST_LineInterpolatePoint(params.geog, fracs.frac, true)::geometry AS geom_4326
                FROM fracs CROSS JOIN params
            ),
            requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            source AS (
                SELECT raster AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId
                  AND id IN (SELECT raster_id FROM requested)
            ),
            merged AS (
                SELECT {aggregateExpression} AS rast
                FROM source
                WHERE rast IS NOT NULL
            )
            SELECT pts.idx,
                   pts.dist_m,
                   pts.line_length_m,
                   ST_Value(
                       merged.rast,
                       1,
                       ST_Transform(ST_SetSRID(pts.geom_4326, {Wgs84Srid}), ST_SRID(merged.rast))
                   ) AS elev
            FROM pts CROSS JOIN merged
            ORDER BY pts.idx
            """;
        AddProfileSqlParameters(command, lineWkb, lineSrid, options);
        command.AddParameter("@layerId", layerId);
        command.AddParameter("@rasterIds", rasterIds);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var dist = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                if (!reader.IsDBNull(2))
                {
                    lineLength = Math.Max(lineLength, reader.GetDouble(2));
                }

                double? elev = reader.IsDBNull(3) ? null : reader.GetDouble(3);
                if (elev.HasValue)
                {
                    allNoData = false;
                }

                samples.Add(new ElevationSample
                {
                    DistanceMeters = dist,
                    Elevation = elev,
                    NoData = !elev.HasValue
                });
            }
        }

        if (samples.Count == 0)
        {
            // Defensive: profile with mosaic merge produced no rows. Return a
            // deterministic 2-sample no-data response rather than an empty list.
            samples.Add(new ElevationSample { DistanceMeters = 0, Elevation = null, NoData = true });
            samples.Add(new ElevationSample { DistanceMeters = lineLength, Elevation = null, NoData = true });
            allNoData = true;
        }

        PostgresElevationLog.ElevationProfileQueried(
            _logger,
            layerId,
            samples.Count,
            lineLength,
            rasterIds.Length,
            allNoData);

        return new ElevationProfileResult
        {
            Samples = samples.ToArray(),
            LineLengthMeters = lineLength,
            SampleCount = samples.Count,
            LayerId = layerId,
            RasterIds = rasterIds,
            SourceSrid = sourceMetadata.SourceSrid,
            PixelType = sourceMetadata.PixelType,
            NoDataValue = sourceMetadata.NoDataValue,
            VerticalUnit = sourceMetadata.VerticalUnit,
            VerticalDatum = sourceMetadata.VerticalDatum,
            IsAllNoData = allNoData
        };
    }

    private static void AddProfileSqlParameters(
        System.Data.Common.DbCommand command,
        byte[] lineWkb,
        int lineSrid,
        ProfileSamplingOptions options)
    {
        command.AddParameter("@lineWkb", lineWkb);
        command.AddParameter("@lineSrid", lineSrid);
        command.AddParameter("@sampleCount", (object?)options.SampleCount ?? DBNull.Value);
        command.AddParameter("@interval", (object?)options.IntervalMeters ?? DBNull.Value);
        command.AddParameter("@defaultSampleCount", options.DefaultSampleCount);
        command.AddParameter("@maxSampleCount", options.MaxSampleCount);
    }

    /// <summary>
    /// SQL fragment that resolves the effective sample count from explicit
    /// <c>@sampleCount</c>, derived <c>@interval</c> against the geodesic line
    /// length, or the configured <c>@defaultSampleCount</c>. Always clamped to
    /// <c>[2, @maxSampleCount]</c>. The derived branch keeps the
    /// <c>length / interval</c> result in <c>float8</c> and clamps against
    /// <c>@maxSampleCount</c> before the <c>::int</c> cast so very long lines
    /// at very small intervals never overflow <c>integer</c>. Nullable
    /// parameters are cast to concrete types so PostgreSQL can resolve them
    /// when the value is NULL.
    /// </summary>
    private static string ResolvedSampleCountExpression =>
        """
        GREATEST(2, LEAST(@maxSampleCount,
            CASE
                WHEN @sampleCount::int IS NOT NULL THEN @sampleCount::int
                WHEN @interval::float8 IS NOT NULL AND @interval::float8 > 0
                    THEN LEAST(
                            @maxSampleCount::float8,
                            CEIL(ST_Length(geog) / @interval::float8) + 1
                         )::int
                ELSE @defaultSampleCount
            END))
        """;

    private static SourceMetadata ResolveSourceMetadata(RasterInfo[] rasters)
    {
        return new SourceMetadata
        {
            SourceSrid = ResolveSingleValue(rasters.Select(static raster => raster.Srid)),
            PixelType = ResolveSingleString(rasters.Select(static raster => raster.PixelType)),
            NoDataValue = ResolveSingleValue(rasters.Select(static raster => raster.NoDataValue)),
            VerticalUnit = null,
            VerticalDatum = null
        };
    }

    private static T? ResolveSingleValue<T>(IEnumerable<T?> values)
        where T : struct
    {
        var distinct = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static string? ResolveSingleString(IEnumerable<string> values)
    {
        var distinct = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static byte[] BuildPointWkb(double x, double y)
    {
        // 2D POINT WKB (little endian).
        var buffer = new byte[21];
        buffer[0] = 1; // little-endian
        // geometry type = 1 (Point)
        buffer[1] = 0x01;
        buffer[2] = 0x00;
        buffer[3] = 0x00;
        buffer[4] = 0x00;
        BitConverter.TryWriteBytes(buffer.AsSpan(5, 8), x);
        BitConverter.TryWriteBytes(buffer.AsSpan(13, 8), y);
        return buffer;
    }

    private readonly record struct SourceMetadata
    {
        public int? SourceSrid { get; init; }

        public string? PixelType { get; init; }

        public double? NoDataValue { get; init; }

        public string? VerticalUnit { get; init; }

        public string? VerticalDatum { get; init; }
    }
}
