// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

internal sealed class PostgresElevationService : IElevationService
{
    private const int DefaultPointSrid = 4326;

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

        if (options.SampleCount < 2)
        {
            throw new ArgumentException("Sample count must be at least 2.", nameof(options));
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

        var samples = new ElevationSample[options.SampleCount];
        var allNoData = true;

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (rasterIds.Length == 0)
        {
            command.CommandText = """
                WITH line AS (
                    SELECT ST_GeomFromWKB(@lineWkb, @lineSrid) AS geom
                ),
                line_g AS (
                    SELECT geom, geom::geography AS geog FROM line
                ),
                params AS (
                    SELECT ST_Length(geog) AS len_m FROM line_g
                ),
                fracs AS (
                    SELECT n, n / (@sampleCount - 1)::float AS frac
                    FROM generate_series(0, @sampleCount - 1) AS gs(n)
                )
                SELECT fracs.n,
                       fracs.frac * params.len_m AS dist_m
                FROM fracs CROSS JOIN params
                ORDER BY fracs.n
                """;
            command.AddParameter("@lineWkb", lineWkb);
            command.AddParameter("@lineSrid", lineSrid);
            command.AddParameter("@sampleCount", options.SampleCount);

            double lineLength = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var n = reader.GetInt32(0);
                var dist = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                lineLength = Math.Max(lineLength, dist);
                samples[n] = new ElevationSample
                {
                    DistanceMeters = dist,
                    Elevation = null,
                    NoData = true
                };
            }

            PostgresElevationLog.ElevationProfileQueried(
                _logger,
                layerId,
                options.SampleCount,
                lineLength,
                rasterCount: 0,
                allNoData: true);

            return new ElevationProfileResult
            {
                Samples = samples,
                LineLengthMeters = lineLength,
                SampleCount = options.SampleCount,
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

        var aggregateExpression = CreateMosaicAggregateExpression(mergeStrategy);
        command.CommandText = $"""
            WITH line AS (
                SELECT ST_GeomFromWKB(@lineWkb, @lineSrid) AS geom
            ),
            line_g AS (
                SELECT geom, geom::geography AS geog FROM line
            ),
            params AS (
                SELECT ST_Length(geog) AS len_m FROM line_g
            ),
            fracs AS (
                SELECT n, n / (@sampleCount - 1)::float AS frac
                FROM generate_series(0, @sampleCount - 1) AS gs(n)
            ),
            pts AS (
                SELECT fracs.n AS idx,
                       fracs.frac * params.len_m AS dist_m,
                       ST_LineInterpolatePoint(line_g.geom, fracs.frac) AS geom
                FROM fracs CROSS JOIN line_g CROSS JOIN params
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
                   ST_Value(
                       merged.rast,
                       1,
                       ST_Transform(ST_SetSRID(pts.geom, @lineSrid), ST_SRID(merged.rast))
                   ) AS elev
            FROM pts CROSS JOIN merged
            ORDER BY pts.idx
            """;
        command.AddParameter("@lineWkb", lineWkb);
        command.AddParameter("@lineSrid", lineSrid);
        command.AddParameter("@sampleCount", options.SampleCount);
        command.AddParameter("@layerId", layerId);
        command.AddParameter("@rasterIds", rasterIds);

        double maxDistance = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var idx = reader.GetInt32(0);
                var dist = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                maxDistance = Math.Max(maxDistance, dist);
                double? elev = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                if (elev.HasValue)
                {
                    allNoData = false;
                }

                samples[idx] = new ElevationSample
                {
                    DistanceMeters = dist,
                    Elevation = elev,
                    NoData = !elev.HasValue
                };
            }
        }

        PostgresElevationLog.ElevationProfileQueried(
            _logger,
            layerId,
            options.SampleCount,
            maxDistance,
            rasterIds.Length,
            allNoData);

        return new ElevationProfileResult
        {
            Samples = samples,
            LineLengthMeters = maxDistance,
            SampleCount = options.SampleCount,
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

    private static string CreateMosaicAggregateExpression(RasterMergeStrategy mergeStrategy) => mergeStrategy switch
    {
        RasterMergeStrategy.Newest => "ST_Union(rast, 'LAST' ORDER BY effective_acquisition ASC, created_at ASC, id ASC)",
        RasterMergeStrategy.Oldest => "ST_Union(rast, 'FIRST' ORDER BY effective_acquisition ASC, created_at ASC, id ASC)",
        RasterMergeStrategy.Average => "ST_Union(rast, 'MEAN')",
        RasterMergeStrategy.Max => "ST_Union(rast, 'MAX')",
        RasterMergeStrategy.Min => "ST_Union(rast, 'MIN')",
        _ => "ST_Union(rast, 'LAST' ORDER BY effective_acquisition ASC, created_at ASC, id ASC)"
    };

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
