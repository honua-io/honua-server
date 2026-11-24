// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Raster;

internal sealed class PostgresElevationService : IElevationService
{
    private const int DefaultPointSrid = 4326;
    private const int Wgs84Srid = 4326;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsRegistry _crsRegistry;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<PostgresElevationService> _logger;
    private readonly string _rasterDataTable;

    public PostgresElevationService(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        IRasterStore rasterStore,
        ILogger<PostgresElevationService> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
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
        var resolvedSrid = srid ?? DefaultPointSrid;
        var pointWkb = BuildPointWkb(x, y);
        var (sourceMetadata, rasterIds) = await ResolveSourceAndIntersectingRastersAsync(
            layerId,
            pointWkb,
            resolvedSrid,
            cancellationToken).ConfigureAwait(false);

        if (rasterIds.Length == 0)
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

        var pixel = await _rasterStore.IdentifyMosaicAsync(
            layerId,
            rasterIds,
            mergeStrategy,
            x,
            y,
            resolvedSrid,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        double? elevation = null;
        if (pixel.HasData && pixel.BandValues.TryGetValue(1, out var raw) && raw is double value)
        {
            elevation = value;
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

        var (sourceMetadata, rasterIds) = await ResolveSourceAndIntersectingRastersAsync(
            layerId,
            lineWkb,
            lineSrid,
            cancellationToken).ConfigureAwait(false);

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

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
        catch (PostgresException exception) when (IsRasterAlignmentFailure(exception))
        {
            throw new ElevationQueryException(
                ElevationFailureKind.MisalignedMosaic,
                "Elevation analysis cannot combine the selected rasters because their pixel grids are not aligned. Reproject or resample the dataset to a common pixel size, origin, and skew before retrying.");
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

    internal static bool IsRasterAlignmentFailure(PostgresException exception)
    {
        const string routine = "rt_raster_from_two_rasters";
        const string alignmentInvariant = "The two rasters provided do not have the same alignment";

        return exception.SqlState == PostgresErrorCodes.InternalError &&
               exception.MessageText.Contains(alignmentInvariant, StringComparison.Ordinal) &&
               (string.Equals(exception.Routine, routine, StringComparison.Ordinal) ||
                exception.MessageText.StartsWith($"{routine}:", StringComparison.Ordinal));
    }

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

    private async Task<(SourceMetadata Metadata, long[] RasterIds)> ResolveSourceAndIntersectingRastersAsync(
        int layerId,
        byte[] geometryWkb,
        int geometrySrid,
        CancellationToken cancellationToken)
    {
        if (_rasterStore is PostgresRasterStore)
        {
            return await ResolveSourceAndIntersectingRastersSqlAsync(
                layerId,
                geometryWkb,
                geometrySrid,
                cancellationToken).ConfigureAwait(false);
        }

        var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No raster data is registered for this elevation dataset.");
        }

        await ValidateSourceRastersAsync(rasters, cancellationToken).ConfigureAwait(false);

        var sourceMetadata = ResolveSourceMetadata(rasters);
        var matching = await _rasterStore.QueryRastersAsync(
            layerId,
            new RasterSelectionQuery
            {
                Geometry = geometryWkb,
                GeometrySrid = geometrySrid
            },
            cancellationToken).ConfigureAwait(false);

        var rasterIds = new long[matching.Length];
        for (var i = 0; i < matching.Length; i++)
        {
            rasterIds[i] = matching[i].Id;
        }

        return (sourceMetadata, rasterIds);
    }

    private async Task<(SourceMetadata Metadata, long[] RasterIds)> ResolveSourceAndIntersectingRastersSqlAsync(
        int layerId,
        byte[] geometryWkb,
        int geometrySrid,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        long rasterCount;
        bool hasInvalidSrid;
        long distinctSridCount;
        int? singleSrid;
        long distinctPixelTypeCount;
        string? singlePixelType;
        long distinctNoDataCount;
        double? singleNoData;

        try
        {
            await using var summaryCommand = connection.CreateCommand();
            summaryCommand.CommandText = $"""
                SELECT COUNT(*) AS raster_count,
                       COALESCE(BOOL_OR(srid IS NULL OR srid <= 0), false) AS has_invalid_srid,
                       COUNT(DISTINCT srid) FILTER (WHERE srid > 0) AS distinct_srid_count,
                       MIN(srid) FILTER (WHERE srid > 0) AS single_srid,
                       COUNT(DISTINCT LOWER(NULLIF(BTRIM(pixel_type), ''))) AS distinct_pixel_type_count,
                       MIN(NULLIF(BTRIM(pixel_type), '')) AS single_pixel_type,
                       COUNT(DISTINCT ST_BandNoDataValue(raster, 1)) FILTER (WHERE ST_BandNoDataValue(raster, 1) IS NOT NULL) AS distinct_nodata_count,
                       MIN(ST_BandNoDataValue(raster, 1)) AS single_nodata
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId
                """;
            summaryCommand.AddParameter("@layerId", layerId);

            await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await summaryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ElevationQueryException(
                    ElevationFailureKind.SourceUnavailable,
                    "No raster data is registered for this elevation dataset.");
            }

            rasterCount = summaryReader.GetInt64(0);
            hasInvalidSrid = summaryReader.GetBoolean(1);
            distinctSridCount = summaryReader.GetInt64(2);
            singleSrid = summaryReader.IsDBNull(3) ? null : summaryReader.GetInt32(3);
            distinctPixelTypeCount = summaryReader.GetInt64(4);
            singlePixelType = summaryReader.IsDBNull(5) ? null : summaryReader.GetString(5);
            distinctNoDataCount = summaryReader.GetInt64(6);
            singleNoData = summaryReader.IsDBNull(7) ? null : summaryReader.GetDouble(7);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No raster data is registered for this elevation dataset.");
        }

        if (rasterCount == 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No raster data is registered for this elevation dataset.");
        }

        if (hasInvalidSrid)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.UnsupportedCrs,
                "One or more source rasters are missing a coordinate reference system (SRID 0 or unset). Re-import the raster with a valid CRS to enable elevation queries.");
        }

        if (distinctSridCount > 1 || !singleSrid.HasValue)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.UnsupportedCrs,
                "Elevation queries require a single source coordinate reference system across all rasters in the dataset.");
        }

        var supported = await _crsRegistry
            .IsSridSupportedAsync(singleSrid.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!supported)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.UnsupportedCrs,
                $"Source raster CRS (SRID {singleSrid.Value}) is not registered or supported by the spatial reference registry.");
        }

        await using var idsCommand = connection.CreateCommand();
        idsCommand.CommandText = $"""
            SELECT id
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId
              AND ST_Intersects(
                  ST_ConvexHull(raster),
                  CASE
                      WHEN srid = @selectionGeomSrid THEN ST_GeomFromWKB(@selectionGeom, @selectionGeomSrid)
                      ELSE ST_Transform(ST_GeomFromWKB(@selectionGeom, @selectionGeomSrid), srid)
                  END)
            ORDER BY COALESCE(acquisition_date, created_at) DESC, created_at DESC, id DESC
            """;
        idsCommand.AddParameter("@layerId", layerId);
        idsCommand.AddParameter("@selectionGeom", geometryWkb);
        idsCommand.AddParameter("@selectionGeomSrid", geometrySrid);

        var ids = new List<long>();
        await using var idsReader = await idsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await idsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(idsReader.GetInt64(0));
        }

        var metadata = new SourceMetadata
        {
            SourceSrid = singleSrid.Value,
            PixelType = distinctPixelTypeCount == 1 ? singlePixelType : null,
            NoDataValue = distinctNoDataCount == 1 ? singleNoData : null,
            VerticalUnit = null,
            VerticalDatum = null
        };

        return (metadata, ids.ToArray());
    }

    private async Task ValidateSourceRastersAsync(
        RasterInfo[] rasters,
        CancellationToken cancellationToken)
    {
        int? firstSrid = null;
        foreach (var raster in rasters)
        {
            if (raster.Srid is not > 0)
            {
                throw new ElevationQueryException(
                    ElevationFailureKind.UnsupportedCrs,
                    "One or more source rasters are missing a coordinate reference system (SRID 0 or unset). Re-import the raster with a valid CRS to enable elevation queries.");
            }

            if (!firstSrid.HasValue)
            {
                firstSrid = raster.Srid.Value;
            }
            else if (firstSrid.Value != raster.Srid.Value)
            {
                throw new ElevationQueryException(
                    ElevationFailureKind.UnsupportedCrs,
                    "Elevation queries require a single source coordinate reference system across all rasters in the dataset.");
            }
        }

        var sourceSrid = firstSrid!.Value;
        var supported = await _crsRegistry
            .IsSridSupportedAsync(sourceSrid, cancellationToken)
            .ConfigureAwait(false);
        if (!supported)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.UnsupportedCrs,
                $"Source raster CRS (SRID {sourceSrid}) is not registered or supported by the spatial reference registry.");
        }
    }

    internal static SourceMetadata ResolveSourceMetadata(RasterInfo[] rasters)
    {
        int? singleSrid = null;
        var sridConflict = false;
        string? singlePixelType = null;
        var pixelTypeConflict = false;
        double? singleNoData = null;
        var noDataConflict = false;

        foreach (var raster in rasters)
        {
            if (!sridConflict && raster.Srid is { } srid)
            {
                if (!singleSrid.HasValue)
                {
                    singleSrid = srid;
                }
                else if (singleSrid.Value != srid)
                {
                    singleSrid = null;
                    sridConflict = true;
                }
            }

            if (!pixelTypeConflict && !string.IsNullOrWhiteSpace(raster.PixelType))
            {
                if (singlePixelType is null)
                {
                    singlePixelType = raster.PixelType;
                }
                else if (!singlePixelType.Equals(raster.PixelType, StringComparison.OrdinalIgnoreCase))
                {
                    singlePixelType = null;
                    pixelTypeConflict = true;
                }
            }

            if (!noDataConflict && raster.NoDataValue is { } noData)
            {
                if (!singleNoData.HasValue)
                {
                    singleNoData = noData;
                }
                else if (singleNoData.Value.CompareTo(noData) != 0)
                {
                    singleNoData = null;
                    noDataConflict = true;
                }
            }
        }

        return new SourceMetadata
        {
            SourceSrid = singleSrid,
            PixelType = singlePixelType,
            NoDataValue = singleNoData,
            VerticalUnit = null,
            VerticalDatum = null
        };
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

    internal readonly record struct SourceMetadata
    {
        public int? SourceSrid { get; init; }

        public string? PixelType { get; init; }

        public double? NoDataValue { get; init; }

        public string? VerticalUnit { get; init; }

        public string? VerticalDatum { get; init; }
    }
}
