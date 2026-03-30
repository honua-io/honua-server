// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostgreSQL-based raster store implementation using PostGIS raster functions.
/// Provides GDAL-free raster operations leveraging SQL-based PostGIS capabilities.
/// </summary>
internal sealed class PostgresRasterStore : IRasterStore
{
    private static readonly FrozenSet<string> _allowedOutputFormats = new[] { "GTiff", "PNG", "JPEG" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> _allowedResamplingAlgorithms = new[] { "NearestNeighbor", "Bilinear", "Cubic", "Lanczos" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresRasterStore> _logger;
    private readonly string _rasterDataTable;
    private readonly string _rasterStatisticsTable;
    private readonly string _rasterTilesTable;

    public PostgresRasterStore(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresRasterStore> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
        _rasterStatisticsTable = SchemaSearchPath.QualifyTable("raster_statistics", schemaName);
        _rasterTilesTable = SchemaSearchPath.QualifyTable("raster_tiles", schemaName);
    }

    /// <inheritdoc />
    public async Task<RasterInfo?> GetRasterInfoAsync(int layerId, long rasterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                   ST_BandNoDataValue(raster, 1) AS nodata_value,
                   ST_UpperLeftX(raster) AS upper_left_x,
                   ST_ScaleX(raster) AS scale_x,
                   ST_SkewX(raster) AS skew_x,
                   ST_UpperLeftY(raster) AS upper_left_y,
                   ST_SkewY(raster) AS skew_y,
                   ST_ScaleY(raster) AS scale_y,
                   ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   created_at, updated_at
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId AND id = @rasterId
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            PostgresRasterLog.RasterNotFound(_logger, layerId, rasterId);
            return null;
        }

        var info = ReadRasterInfo(reader);
        PostgresRasterLog.RasterInfoRetrieved(_logger, layerId, rasterId, info.Width, info.Height);
        return info;
    }

    /// <inheritdoc />
    public async Task<RasterResult> ExportImageAsync(int layerId, long rasterId, RasterQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var formatName = query.OutputFormat.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        // Build raster expression with chained transformations
        var rasterExpr = "raster";
        var extraParams = new List<(string Name, object Value)>();

        // 1. Clip to region if specified
        if (query.ClipRegion is { } clip)
        {
            if (clip.Srid.HasValue && clip.Srid.Value > 0)
            {
                rasterExpr = $"ST_Clip({rasterExpr}, ST_Transform(ST_GeomFromWKB(@clipGeom, @clipSrid), ST_SRID(raster)))";
                extraParams.Add(("@clipSrid", clip.Srid.Value));
            }
            else
            {
                rasterExpr = $"ST_Clip({rasterExpr}, ST_GeomFromWKB(@clipGeom, ST_SRID(raster)))";
            }

            extraParams.Add(("@clipGeom", clip.Geometry));
        }

        // 2. Resize to output dimensions if specified
        if (query.OutputWidth is > 0 && query.OutputHeight is > 0)
        {
            rasterExpr = $"ST_Resize({rasterExpr}, @outputWidth, @outputHeight)";
            extraParams.Add(("@outputWidth", query.OutputWidth.Value));
            extraParams.Add(("@outputHeight", query.OutputHeight.Value));
        }
        else if (query.PixelSize is { } pixelSize)
        {
            var algorithm = query.ResamplingAlgorithm switch
            {
                ResamplingAlgorithm.NearestNeighbor => "NearestNeighbor",
                ResamplingAlgorithm.Bilinear => "Bilinear",
                ResamplingAlgorithm.Bicubic => "Cubic",
                ResamplingAlgorithm.Lanczos => "Lanczos",
                _ => "NearestNeighbor"
            };
            if (!_allowedResamplingAlgorithms.Contains(algorithm))
            {
                throw new ArgumentException($"Unsupported resampling algorithm: {algorithm}");
            }

            rasterExpr = $"ST_Rescale({rasterExpr}, @pixelW, @pixelH, '{algorithm}')";
            extraParams.Add(("@pixelW", pixelSize.Width));
            extraParams.Add(("@pixelH", pixelSize.Height));
        }

        // 3. Reproject output if requested
        if (query.OutputSrid.HasValue && query.OutputSrid.Value > 0)
        {
            rasterExpr = $"ST_Transform({rasterExpr}, @outputSrid)";
            extraParams.Add(("@outputSrid", query.OutputSrid.Value));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH transformed AS (
                SELECT {rasterExpr} AS rast
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            )
            SELECT ST_AsGDALRaster(rast, '{formatName}') AS data,
                   ST_Width(rast) AS width,
                   ST_Height(rast) AS height,
                   ST_SRID(rast) AS srid,
                   ST_NumBands(rast) AS band_count,
                   ST_XMin(ST_Envelope(rast)) AS xmin,
                   ST_YMin(ST_Envelope(rast)) AS ymin,
                   ST_XMax(ST_Envelope(rast)) AS xmax,
                   ST_YMax(ST_Envelope(rast)) AS ymax
            FROM transformed
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);
        foreach (var (name, value) in extraParams)
        {
            AddParameter(command, name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            PostgresRasterLog.RasterNotFound(_logger, layerId, rasterId);
            return new RasterResult
            {
                Data = Array.Empty<byte>(),
                ContentType = query.OutputFormat.ToContentType(),
                Width = 0,
                Height = 0
            };
        }

        var dataOrd = reader.GetOrdinal("data");
        var widthOrd = reader.GetOrdinal("width");
        var heightOrd = reader.GetOrdinal("height");
        var sridOrd = reader.GetOrdinal("srid");
        var bandCountOrd = reader.GetOrdinal("band_count");
        var xminOrd = reader.GetOrdinal("xmin");
        var yminOrd = reader.GetOrdinal("ymin");
        var xmaxOrd = reader.GetOrdinal("xmax");
        var ymaxOrd = reader.GetOrdinal("ymax");

        var data = reader.IsDBNull(dataOrd) ? Array.Empty<byte>() : (byte[])reader[dataOrd];
        var width = reader.GetInt32(widthOrd);
        var height = reader.GetInt32(heightOrd);
        var srid = reader.GetInt32(sridOrd);
        var bandCount = reader.GetInt32(bandCountOrd);
        var extent = new RasterExtent
        {
            XMin = reader.GetDouble(xminOrd),
            YMin = reader.GetDouble(yminOrd),
            XMax = reader.GetDouble(xmaxOrd),
            YMax = reader.GetDouble(ymaxOrd),
            Srid = srid
        };

        PostgresRasterLog.ImageExported(_logger, layerId, rasterId, width, height, data.Length);

        return new RasterResult
        {
            Data = data,
            ContentType = query.OutputFormat.ToContentType(),
            Width = width,
            Height = height,
            Srid = srid,
            BandCount = bandCount,
            Extent = extent
        };
    }

    /// <inheritdoc />
    public async Task<PixelValueResult> IdentifyAsync(int layerId, long rasterId, double x, double y, int? srid = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var pointSrid = srid ?? 4326;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT band, val
            FROM {_rasterDataTable},
                 LATERAL generate_series(1, ST_NumBands(raster)) AS band,
                 LATERAL ST_Value(raster, band,
                    ST_Transform(ST_SetSRID(ST_MakePoint(@x, @y), @pointSrid), ST_SRID(raster))
                 ) AS val
            WHERE layer_id = @layerId AND id = @rasterId
            ORDER BY band
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);
        AddParameter(command, "@x", x);
        AddParameter(command, "@y", y);
        AddParameter(command, "@pointSrid", pointSrid);

        var bandValues = new Dictionary<int, object?>();
        var hasData = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var band = reader.GetInt32(0);
            var val = reader.IsDBNull(1) ? null : (object?)reader.GetDouble(1);
            bandValues[band] = val;
            if (val != null)
            {
                hasData = true;
            }
        }

        PostgresRasterLog.PixelValueIdentified(_logger, layerId, rasterId, x, y, hasData, bandValues.Count);

        return new PixelValueResult
        {
            X = x,
            Y = y,
            Srid = srid,
            BandValues = bandValues,
            HasData = hasData
        };
    }

    /// <inheritdoc />
    public async Task<RasterResult?> GetImageTileAsync(int layerId, long rasterId, int level, int row, int col, RasterFormat format = RasterFormat.PNG, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Check pre-computed tiles first
        await using var tileCommand = connection.CreateCommand();
        tileCommand.CommandText = $"""
            SELECT tile_data, content_type
            FROM {_rasterTilesTable}
            WHERE raster_data_id = @rasterId AND zoom_level = @level AND tile_x = @col AND tile_y = @row
            """;
        AddParameter(tileCommand, "@rasterId", rasterId);
        AddParameter(tileCommand, "@level", level);
        AddParameter(tileCommand, "@col", col);
        AddParameter(tileCommand, "@row", row);

        await using var tileReader = await tileCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await tileReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tileDataOrd = tileReader.GetOrdinal("tile_data");
            var contentTypeOrd = tileReader.GetOrdinal("content_type");
            var tileData = (byte[])tileReader[tileDataOrd];
            var contentType = tileReader.GetString(contentTypeOrd);
            PostgresRasterLog.TileGenerated(_logger, layerId, rasterId, level, row, col, tileData.Length);
            return new RasterResult
            {
                Data = tileData,
                ContentType = contentType,
                Width = 256,
                Height = 256,
                Srid = 3857
            };
        }

        // Dynamic tile generation via PostGIS
        var formatName = format.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        await using var dynCommand = connection.CreateCommand();
        dynCommand.CommandText = $"""
            WITH tile_bounds AS (
                SELECT ST_TileEnvelope(@level, @col, @row) AS geom
            )
            SELECT ST_AsGDALRaster(
                ST_Resize(
                    ST_Clip(raster, ST_Transform(tb.geom, ST_SRID(raster))),
                    256, 256
                ),
                '{formatName}'
            ) AS data
            FROM {_rasterDataTable}, tile_bounds tb
            WHERE layer_id = @layerId AND id = @rasterId
              AND ST_Intersects(ST_ConvexHull(raster), ST_Transform(tb.geom, ST_SRID(raster)))
            """;
        AddParameter(dynCommand, "@layerId", layerId);
        AddParameter(dynCommand, "@rasterId", rasterId);
        AddParameter(dynCommand, "@level", level);
        AddParameter(dynCommand, "@col", col);
        AddParameter(dynCommand, "@row", row);

        var result = await dynCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not byte[] data || data.Length == 0)
        {
            return null;
        }

        PostgresRasterLog.TileGenerated(_logger, layerId, rasterId, level, row, col, data.Length);
        return new RasterResult
        {
            Data = data,
            ContentType = format.ToContentType(),
            Width = 256,
            Height = 256,
            Srid = 3857
        };
    }

    /// <inheritdoc />
    public async Task<RasterStatistics[]> GetStatisticsAsync(int layerId, long rasterId, int[]? bands = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Try cached statistics first
        await using var cachedCommand = connection.CreateCommand();
        cachedCommand.CommandText = $"""
            SELECT band_number, min_value, max_value, mean_value, std_dev,
                   valid_pixel_count, nodata_pixel_count
            FROM {_rasterStatisticsTable}
            WHERE raster_data_id = @rasterId
            ORDER BY band_number
            """;
        AddParameter(cachedCommand, "@rasterId", rasterId);

        var stats = new List<RasterStatistics>();
        await using var cachedReader = await cachedCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await cachedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stats.Add(new RasterStatistics
            {
                Band = cachedReader.GetInt32(0),
                MinValue = cachedReader.IsDBNull(1) ? null : cachedReader.GetDouble(1),
                MaxValue = cachedReader.IsDBNull(2) ? null : cachedReader.GetDouble(2),
                MeanValue = cachedReader.IsDBNull(3) ? null : cachedReader.GetDouble(3),
                StandardDeviation = cachedReader.IsDBNull(4) ? null : cachedReader.GetDouble(4),
                ValidPixelCount = cachedReader.IsDBNull(5) ? 0 : cachedReader.GetInt64(5),
                NoDataPixelCount = cachedReader.IsDBNull(6) ? 0 : cachedReader.GetInt64(6)
            });
        }

        if (stats.Count > 0)
        {
            if (bands != null)
            {
                stats = stats.Where(s => bands.Contains(s.Band)).ToList();
            }

            PostgresRasterLog.StatisticsCalculated(_logger, layerId, rasterId, stats.Count);
            return stats.ToArray();
        }

        // Compute statistics dynamically via PostGIS ST_SummaryStats
        await using var computeCommand = connection.CreateCommand();
        computeCommand.CommandText = $"""
            SELECT band,
                   (stats).min AS min_value,
                   (stats).max AS max_value,
                   (stats).mean AS mean_value,
                   (stats).stddev AS std_dev,
                   (stats).count AS valid_count
            FROM (
                SELECT generate_series(1, ST_NumBands(raster)) AS band,
                       ST_SummaryStats(raster, generate_series(1, ST_NumBands(raster))) AS stats
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            ) sub
            ORDER BY band
            """;
        AddParameter(computeCommand, "@layerId", layerId);
        AddParameter(computeCommand, "@rasterId", rasterId);

        await using var computeReader = await computeCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await computeReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stats.Add(new RasterStatistics
            {
                Band = computeReader.GetInt32(0),
                MinValue = computeReader.IsDBNull(1) ? null : computeReader.GetDouble(1),
                MaxValue = computeReader.IsDBNull(2) ? null : computeReader.GetDouble(2),
                MeanValue = computeReader.IsDBNull(3) ? null : computeReader.GetDouble(3),
                StandardDeviation = computeReader.IsDBNull(4) ? null : computeReader.GetDouble(4),
                ValidPixelCount = computeReader.IsDBNull(5) ? 0 : computeReader.GetInt64(5),
                NoDataPixelCount = 0
            });
        }

        if (bands != null)
        {
            stats = stats.Where(s => bands.Contains(s.Band)).ToList();
        }

        PostgresRasterLog.StatisticsCalculated(_logger, layerId, rasterId, stats.Count);
        return stats.ToArray();
    }

    /// <inheritdoc />
    public async Task<RasterExtent?> GetExtentAsync(int layerId, long rasterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   ST_SRID(raster) AS srid
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId AND id = @rasterId
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RasterExtent
        {
            XMin = reader.GetDouble(0),
            YMin = reader.GetDouble(1),
            XMax = reader.GetDouble(2),
            YMax = reader.GetDouble(3),
            Srid = reader.GetInt32(4)
        };
    }

    /// <inheritdoc />
    public async Task<RasterInfo?> GetPrimaryRasterInfoAsync(int layerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                   ST_BandNoDataValue(raster, 1) AS nodata_value,
                   ST_UpperLeftX(raster) AS upper_left_x,
                   ST_ScaleX(raster) AS scale_x,
                   ST_SkewX(raster) AS skew_x,
                   ST_UpperLeftY(raster) AS upper_left_y,
                   ST_SkewY(raster) AS skew_y,
                   ST_ScaleY(raster) AS scale_y,
                   ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   created_at, updated_at
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId
            ORDER BY created_at DESC
            LIMIT 1
            """;
        AddParameter(command, "@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            PostgresRasterLog.RasterListRetrieved(_logger, layerId, 0);
            return null;
        }

        var info = ReadRasterInfo(reader);
        PostgresRasterLog.RasterInfoRetrieved(_logger, layerId, info.Id, info.Width, info.Height);
        return info;
    }

    /// <inheritdoc />
    public async Task<RasterInfo[]> ListRastersAsync(int layerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                   ST_BandNoDataValue(raster, 1) AS nodata_value,
                   ST_UpperLeftX(raster) AS upper_left_x,
                   ST_ScaleX(raster) AS scale_x,
                   ST_SkewX(raster) AS skew_x,
                   ST_UpperLeftY(raster) AS upper_left_y,
                   ST_SkewY(raster) AS skew_y,
                   ST_ScaleY(raster) AS scale_y,
                   ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   created_at, updated_at
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId
            ORDER BY created_at DESC
            """;
        AddParameter(command, "@layerId", layerId);

        var rasters = new List<RasterInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rasters.Add(ReadRasterInfo(reader));
        }

        PostgresRasterLog.RasterListRetrieved(_logger, layerId, rasters.Count);
        return rasters.ToArray();
    }

    // =============================================================================
    // Private helpers
    // =============================================================================

    private static RasterInfo ReadRasterInfo(DbDataReader reader)
    {
        // Cache ordinals once (avoids 19 repeated column-name lookups per row)
        var idOrd = reader.GetOrdinal("id");
        var layerIdOrd = reader.GetOrdinal("layer_id");
        var nameOrd = reader.GetOrdinal("name");
        var widthOrd = reader.GetOrdinal("width");
        var heightOrd = reader.GetOrdinal("height");
        var bandCountOrd = reader.GetOrdinal("band_count");
        var pixelTypeOrd = reader.GetOrdinal("pixel_type");
        var sridOrd = reader.GetOrdinal("srid");
        var noDataOrd = reader.GetOrdinal("nodata_value");
        var upperLeftXOrd = reader.GetOrdinal("upper_left_x");
        var scaleXOrd = reader.GetOrdinal("scale_x");
        var skewXOrd = reader.GetOrdinal("skew_x");
        var upperLeftYOrd = reader.GetOrdinal("upper_left_y");
        var skewYOrd = reader.GetOrdinal("skew_y");
        var scaleYOrd = reader.GetOrdinal("scale_y");
        var xminOrd = reader.GetOrdinal("xmin");
        var yminOrd = reader.GetOrdinal("ymin");
        var xmaxOrd = reader.GetOrdinal("xmax");
        var ymaxOrd = reader.GetOrdinal("ymax");
        var createdAtOrd = reader.GetOrdinal("created_at");
        var updatedOrd = reader.GetOrdinal("updated_at");

        var geoTransform = new[]
        {
            reader.GetDouble(upperLeftXOrd),
            reader.GetDouble(scaleXOrd),
            reader.GetDouble(skewXOrd),
            reader.GetDouble(upperLeftYOrd),
            reader.GetDouble(skewYOrd),
            reader.GetDouble(scaleYOrd)
        };

        return new RasterInfo
        {
            Id = reader.GetInt64(idOrd),
            LayerId = reader.GetInt32(layerIdOrd),
            Name = reader.GetString(nameOrd),
            Width = reader.GetInt32(widthOrd),
            Height = reader.GetInt32(heightOrd),
            BandCount = reader.GetInt32(bandCountOrd),
            PixelType = reader.GetString(pixelTypeOrd),
            Srid = reader.GetInt32(sridOrd),
            NoDataValue = reader.IsDBNull(noDataOrd) ? null : reader.GetDouble(noDataOrd),
            GeoTransform = geoTransform,
            Extent = new RasterExtent
            {
                XMin = reader.GetDouble(xminOrd),
                YMin = reader.GetDouble(yminOrd),
                XMax = reader.GetDouble(xmaxOrd),
                YMax = reader.GetDouble(ymaxOrd),
                Srid = reader.GetInt32(sridOrd)
            },
            CreatedAt = reader.GetDateTime(createdAtOrd),
            ModifiedAt = reader.IsDBNull(updatedOrd) ? null : reader.GetDateTime(updatedOrd)
        };
    }

    private static void AddParameter(DbCommand command, string name, object value)
        => command.AddParameter(name, value);
}
