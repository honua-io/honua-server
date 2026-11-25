// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Raster;

internal sealed partial class PostgresElevationService
{
    private readonly struct RasterSelectionRequest
    {
        public required int LayerId { get; init; }
        public required byte[] GeometryWkb { get; init; }
        public required int GeometrySrid { get; init; }
    }

    private readonly struct RasterLayerSummary
    {
        public required long RasterCount { get; init; }
        public required bool HasInvalidSrid { get; init; }
        public required long DistinctSridCount { get; init; }
        public int? SingleSrid { get; init; }
        public required long DistinctPixelTypeCount { get; init; }
        public string? SinglePixelType { get; init; }
        public required long DistinctNoDataCount { get; init; }
        public double? SingleNoData { get; init; }
    }

    private Task<(SourceMetadata Metadata, long[] RasterIds)> ResolveSourceAndIntersectingRastersAsync(
        RasterSelectionRequest request,
        CancellationToken cancellationToken)
        => _rasterStore is PostgresRasterStore
            ? ResolveSourceAndIntersectingRastersSqlAsync(request, cancellationToken)
            : ResolveSourceViaStoreAsync(request, cancellationToken);

    private async Task<(SourceMetadata Metadata, long[] RasterIds)> ResolveSourceViaStoreAsync(
        RasterSelectionRequest request,
        CancellationToken cancellationToken)
    {
        var rasters = await _rasterStore.ListRastersAsync(request.LayerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No raster data is registered for this elevation dataset.");
        }

        await ValidateSourceRastersAsync(rasters, cancellationToken).ConfigureAwait(false);

        var sourceMetadata = ResolveSourceMetadata(rasters);
        var matching = await _rasterStore.QueryRastersAsync(
            request.LayerId,
            new RasterSelectionQuery
            {
                Geometry = request.GeometryWkb,
                GeometrySrid = request.GeometrySrid
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
        RasterSelectionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var summary = await ReadRasterLayerSummaryAsync(connection, request.LayerId, cancellationToken).ConfigureAwait(false);
        await ValidateRasterLayerSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
        var ids = await QueryIntersectingRasterIdsAsync(connection, request, cancellationToken).ConfigureAwait(false);

        var metadata = new SourceMetadata
        {
            SourceSrid = summary.SingleSrid!.Value,
            PixelType = summary.DistinctPixelTypeCount == 1 ? summary.SinglePixelType : null,
            NoDataValue = summary.DistinctNoDataCount == 1 ? summary.SingleNoData : null,
            VerticalUnit = null,
            VerticalDatum = null
        };

        return (metadata, ids);
    }

    private async Task<RasterLayerSummary> ReadRasterLayerSummaryAsync(
        DbConnection connection,
        int layerId,
        CancellationToken cancellationToken)
    {
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

            await using var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ElevationQueryException(ElevationFailureKind.SourceUnavailable, "No raster data is registered for this elevation dataset.");
            }

            return MapRasterLayerSummary(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new ElevationQueryException(ElevationFailureKind.SourceUnavailable, "No raster data is registered for this elevation dataset.");
        }
    }

    private static RasterLayerSummary MapRasterLayerSummary(DbDataReader reader) => new()
    {
        RasterCount = reader.GetInt64(0),
        HasInvalidSrid = reader.GetBoolean(1),
        DistinctSridCount = reader.GetInt64(2),
        SingleSrid = reader.IsDBNull(3) ? null : reader.GetInt32(3),
        DistinctPixelTypeCount = reader.GetInt64(4),
        SinglePixelType = reader.IsDBNull(5) ? null : reader.GetString(5),
        DistinctNoDataCount = reader.GetInt64(6),
        SingleNoData = reader.IsDBNull(7) ? null : reader.GetDouble(7)
    };

    private async Task ValidateRasterLayerSummaryAsync(
        RasterLayerSummary summary,
        CancellationToken cancellationToken)
    {
        if (summary.RasterCount == 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No raster data is registered for this elevation dataset.");
        }

        if (summary.HasInvalidSrid)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.UnsupportedCrs,
                "One or more source rasters are missing a coordinate reference system (SRID 0 or unset). Re-import the raster with a valid CRS to enable elevation queries.");
        }

        if (summary.DistinctSridCount > 1 || !summary.SingleSrid.HasValue)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.UnsupportedCrs,
                "Elevation queries require a single source coordinate reference system across all rasters in the dataset.");
        }

        var supported = await _crsRegistry.IsSridSupportedAsync(summary.SingleSrid.Value, cancellationToken).ConfigureAwait(false);
        if (!supported)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.UnsupportedCrs,
                $"Source raster CRS (SRID {summary.SingleSrid.Value}) is not registered or supported by the spatial reference registry.");
        }
    }

    private async Task<long[]> QueryIntersectingRasterIdsAsync(
        DbConnection connection,
        RasterSelectionRequest request,
        CancellationToken cancellationToken)
    {
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
        idsCommand.AddParameter("@layerId", request.LayerId);
        idsCommand.AddParameter("@selectionGeom", request.GeometryWkb);
        idsCommand.AddParameter("@selectionGeomSrid", request.GeometrySrid);

        var ids = new List<long>();
        await using var idsReader = await idsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await idsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(idsReader.GetInt64(0));
        }

        return ids.ToArray();
    }

    private struct SourceMetadataAccumulator
    {
        private int? _singleSrid;
        private bool _sridConflict;
        private string? _singlePixelType;
        private bool _pixelTypeConflict;
        private double? _singleNoData;
        private bool _noDataConflict;

        public void Observe(RasterInfo raster)
        {
            ObserveSrid(raster.Srid);
            ObservePixelType(raster.PixelType);
            ObserveNoData(raster.NoDataValue);
        }

        public readonly SourceMetadata ToMetadata() => new()
        {
            SourceSrid = _singleSrid,
            PixelType = _singlePixelType,
            NoDataValue = _singleNoData,
            VerticalUnit = null,
            VerticalDatum = null
        };

        private void ObserveSrid(int? candidate)
        {
            if (_sridConflict || candidate is not { } srid)
            {
                return;
            }

            if (!_singleSrid.HasValue)
            {
                _singleSrid = srid;
            }
            else if (_singleSrid.Value != srid)
            {
                _singleSrid = null;
                _sridConflict = true;
            }
        }

        private void ObservePixelType(string? candidate)
        {
            if (_pixelTypeConflict || string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (_singlePixelType is null)
            {
                _singlePixelType = candidate;
            }
            else if (!_singlePixelType.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                _singlePixelType = null;
                _pixelTypeConflict = true;
            }
        }

        private void ObserveNoData(double? candidate)
        {
            if (_noDataConflict || candidate is not { } noData)
            {
                return;
            }

            if (!_singleNoData.HasValue)
            {
                _singleNoData = noData;
            }
            else if (_singleNoData.Value.CompareTo(noData) != 0)
            {
                _singleNoData = null;
                _noDataConflict = true;
            }
        }
    }
}
