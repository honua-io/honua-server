// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
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
    private static readonly FrozenSet<string> _allowedOutputFormats = new[] { "GTiff", "PNG", "JPEG", "COG" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> _allowedResamplingAlgorithms = new[] { "NearestNeighbor", "Bilinear", "Cubic", "Lanczos" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> _allowedZonalStatistics = new[] { "count", "sum", "mean", "min", "max", "stddev", "variance" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresRasterStore> _logger;
    private readonly string _rasterDataTable;
    private readonly string _rasterStatisticsTable;
    private readonly string _rasterTilesTable;
    private readonly string _featuresTable;

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
        _featuresTable = SchemaSearchPath.QualifyTable(DatabaseSchema.FeaturesTable, schemaName);
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

        // COG export: use creation options for proper internal tiling.
        // If the COG GDAL driver is unavailable, fall back to GTiff with COG-compatible options.
        var creationOptionsClause = "";
        var effectiveFormat = formatName;
        const int exportBlockSize = 512;
        if (formatName == "COG")
        {
            (effectiveFormat, creationOptionsClause) = await ResolveCogOptionsAsync(
                connection, blockSize: exportBlockSize, layerId, rasterId,
                includeOverviewResampling: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            creationOptionsClause = BuildCreationOptionsClause(BuildExportCreationOptions(query, effectiveFormat));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH transformed AS (
                SELECT {rasterExpr} AS rast
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            )
            SELECT ST_AsGDALRaster(rast, '{effectiveFormat}'{creationOptionsClause}) AS data,
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

    internal static string[] BuildExportCreationOptions(RasterQuery query, string formatName)
    {
        var options = new List<string>();

        if (string.Equals(formatName, "JPEG", StringComparison.Ordinal))
        {
            if (query.Quality.HasValue)
            {
                options.Add(string.Create(CultureInfo.InvariantCulture, $"QUALITY={query.Quality.Value}"));
            }

            return [.. options];
        }

        if (!string.Equals(formatName, "GTiff", StringComparison.Ordinal) || !query.TiffCompression.HasValue)
        {
            return [.. options];
        }

        switch (query.TiffCompression.Value)
        {
            case TiffCompression.None:
                options.Add("COMPRESS=NONE");
                break;
            case TiffCompression.LZW:
                options.Add("COMPRESS=LZW");
                break;
            case TiffCompression.Deflate:
                options.Add("COMPRESS=DEFLATE");
                break;
            case TiffCompression.JPEG:
                options.Add("COMPRESS=JPEG");
                if (query.Quality.HasValue)
                {
                    options.Add(string.Create(CultureInfo.InvariantCulture, $"JPEG_QUALITY={query.Quality.Value}"));
                }
                break;
        }

        return [.. options];
    }

    private static string BuildCreationOptionsClause(string[] creationOptions)
    {
        if (creationOptions.Length == 0)
        {
            return string.Empty;
        }

        var quotedOptions = creationOptions.Select(static option => $"'{option}'");
        return $", ARRAY[{string.Join(", ", quotedOptions)}]";
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

        // COG driver check: same fallback as ExportImageAsync
        var effectiveTileFormat = formatName;
        var tileCreationOptions = "";
        if (formatName == "COG")
        {
            (effectiveTileFormat, tileCreationOptions) = await ResolveCogOptionsAsync(
                connection, blockSize: 256, layerId, rasterId,
                includeOverviewResampling: false, cancellationToken).ConfigureAwait(false);
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
                '{effectiveTileFormat}'{tileCreationOptions}
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
    public async Task<RasterHistogram[]> GetHistogramsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default)
    {
        // Clamp the bin count to a sensible range so abusive callers can't
        // request 100 000-bucket histograms. 256 matches ArcGIS Pro defaults.
        if (binCount <= 0)
        {
            binCount = 256;
        }
        else if (binCount > 1024)
        {
            binCount = 1024;
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Resolve band list (defer to ST_NumBands when caller supplied none).
        int[] effectiveBands;
        if (bands is { Length: > 0 })
        {
            effectiveBands = bands;
        }
        else
        {
            await using var bandsCommand = connection.CreateCommand();
            bandsCommand.CommandText = $"""
                SELECT ST_NumBands(raster) AS band_count
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
                """;
            AddParameter(bandsCommand, "@layerId", layerId);
            AddParameter(bandsCommand, "@rasterId", rasterId);
            var bandCountResult = await bandsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (bandCountResult is null or DBNull)
            {
                return Array.Empty<RasterHistogram>();
            }

            var totalBands = bandCountResult switch
            {
                int intValue => intValue,
                long longValue => checked((int)longValue),
                _ => Convert.ToInt32(bandCountResult, System.Globalization.CultureInfo.InvariantCulture)
            };
            effectiveBands = new int[totalBands];
            for (var i = 0; i < totalBands; i++)
            {
                effectiveBands[i] = i + 1;
            }
        }

        // Fast path: compute every band's histogram in a single SQL round-trip via
        // a LATERAL join over unnest(@bands). For an N-band raster this collapses N
        // network round-trips into 1. PostGIS ST_Histogram can fail on uniform-value
        // rasters, so on any error we fall back to the per-band loop below which
        // preserves the original partial-failure behaviour.
        var batched = await TryGetHistogramsBatchedAsync(
            connection, layerId, rasterId, effectiveBands, binCount, cancellationToken).ConfigureAwait(false);
        if (batched is not null)
        {
            return batched;
        }

        var results = new List<RasterHistogram>(effectiveBands.Length);
        foreach (var band in effectiveBands)
        {
            await using var histogramCommand = connection.CreateCommand();
            histogramCommand.CommandText = $"""
                SELECT (h).min AS bin_min,
                       (h).max AS bin_max,
                       (h).count AS bin_count
                FROM (
                    SELECT ST_Histogram(raster, @band, @binCount, false) AS h
                    FROM {_rasterDataTable}
                    WHERE layer_id = @layerId AND id = @rasterId
                ) sub
                """;
            AddParameter(histogramCommand, "@layerId", layerId);
            AddParameter(histogramCommand, "@rasterId", rasterId);
            AddParameter(histogramCommand, "@band", band);
            AddParameter(histogramCommand, "@binCount", binCount);

            var counts = new long[binCount];
            double overallMin = double.NaN;
            double overallMax = double.NaN;
            var index = 0;

            try
            {
                await using var histogramReader = await histogramCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await histogramReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var binMin = histogramReader.IsDBNull(0) ? double.NaN : histogramReader.GetDouble(0);
                    var binMax = histogramReader.IsDBNull(1) ? double.NaN : histogramReader.GetDouble(1);
                    var count = histogramReader.IsDBNull(2) ? 0L :
                        histogramReader.GetValue(2) switch
                        {
                            long longValue => longValue,
                            int intValue => (long)intValue,
                            _ => Convert.ToInt64(histogramReader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture)
                        };

                    if (index < counts.Length)
                    {
                        counts[index] = count;
                    }

                    if (index == 0)
                    {
                        overallMin = binMin;
                    }

                    if (!double.IsNaN(binMax))
                    {
                        overallMax = binMax;
                    }

                    index++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // PostGIS ST_Histogram fails on uniform-value rasters; surface an empty
                // histogram so the endpoint stays well-shaped instead of 500ing.
                PostgresRasterLog.HistogramFailed(_logger, ex, layerId, rasterId, band);
                results.Add(new RasterHistogram
                {
                    Band = band,
                    BinCount = 0,
                    Min = 0,
                    Max = 0,
                    Counts = Array.Empty<long>()
                });
                continue;
            }

            // Trim trailing slots if PostGIS produced fewer bins than requested.
            if (index < binCount)
            {
                Array.Resize(ref counts, index);
            }

            results.Add(new RasterHistogram
            {
                Band = band,
                BinCount = counts.Length,
                Min = double.IsNaN(overallMin) ? 0 : overallMin,
                Max = double.IsNaN(overallMax) ? 0 : overallMax,
                Counts = counts
            });
        }

        return results.ToArray();
    }

    /// <summary>
    /// Computes per-band histograms in a single SQL statement using a LATERAL join over
    /// <c>unnest(@bands)</c>. Returns <c>null</c> when the batched query fails (e.g. PostGIS
    /// rejects ST_Histogram on a uniform-value band) so the caller can fall back to the
    /// per-band loop that preserves partial-failure semantics.
    /// </summary>
    private async Task<RasterHistogram[]?> TryGetHistogramsBatchedAsync(
        DbConnection connection,
        int layerId,
        long rasterId,
        int[] effectiveBands,
        int binCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var batchCommand = connection.CreateCommand();
            batchCommand.CommandText = $"""
                SELECT b.band,
                       (h).min   AS bin_min,
                       (h).max   AS bin_max,
                       (h).count AS bin_count
                FROM (
                    SELECT raster
                    FROM {_rasterDataTable}
                    WHERE layer_id = @layerId AND id = @rasterId
                ) r
                CROSS JOIN unnest(@bands) WITH ORDINALITY AS b(band, ord)
                CROSS JOIN LATERAL ST_Histogram(r.raster, b.band, @binCount, false) AS h
                ORDER BY b.ord
                """;
            AddParameter(batchCommand, "@layerId", layerId);
            AddParameter(batchCommand, "@rasterId", rasterId);
            AddParameter(batchCommand, "@bands", effectiveBands);
            AddParameter(batchCommand, "@binCount", binCount);

            // Each band collects its bins as it streams from the reader. We key by band
            // so we cope with PostGIS returning rows in the LATERAL-join order.
            var bandBuckets = new Dictionary<int, (List<long> Counts, double Min, double Max)>(effectiveBands.Length);

            await using var batchReader = await batchCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await batchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var band = batchReader.GetInt32(0);
                var binMin = batchReader.IsDBNull(1) ? double.NaN : batchReader.GetDouble(1);
                var binMax = batchReader.IsDBNull(2) ? double.NaN : batchReader.GetDouble(2);
                var count = batchReader.IsDBNull(3) ? 0L :
                    batchReader.GetValue(3) switch
                    {
                        long longValue => longValue,
                        int intValue => (long)intValue,
                        _ => Convert.ToInt64(batchReader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)
                    };

                if (!bandBuckets.TryGetValue(band, out var bucket))
                {
                    bucket = (new List<long>(binCount), double.IsNaN(binMin) ? 0 : binMin, 0);
                }

                bucket.Counts.Add(count);
                if (!double.IsNaN(binMax))
                {
                    bucket = (bucket.Counts, bucket.Min, binMax);
                }
                bandBuckets[band] = bucket;
            }

            var results = new RasterHistogram[effectiveBands.Length];
            for (var i = 0; i < effectiveBands.Length; i++)
            {
                var band = effectiveBands[i];
                if (bandBuckets.TryGetValue(band, out var bucket) && bucket.Counts.Count > 0)
                {
                    results[i] = new RasterHistogram
                    {
                        Band = band,
                        BinCount = bucket.Counts.Count,
                        Min = bucket.Min,
                        Max = bucket.Max,
                        Counts = bucket.Counts.ToArray(),
                    };
                }
                else
                {
                    results[i] = new RasterHistogram
                    {
                        Band = band,
                        BinCount = 0,
                        Min = 0,
                        Max = 0,
                        Counts = Array.Empty<long>(),
                    };
                }
            }

            return results;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            PostgresRasterLog.HistogramBatchFallback(_logger, ex, layerId, rasterId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<RasterZonalStatisticsRow[]> ComputeZonalStatisticsAsync(
        int layerId,
        long rasterId,
        int zonesLayerId,
        int band,
        IReadOnlyList<string> statistics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (band < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "Band index must be >= 1.");
        }

        // Dedupe + canonicalize (lowercase) stat names, preserving caller order for output keys.
        var requestedStats = new List<string>(statistics.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in statistics)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("Statistic name cannot be null or whitespace.", nameof(statistics));
            }

            var normalized = raw.Trim().ToLowerInvariant();
            if (!_allowedZonalStatistics.Contains(normalized))
            {
                throw new ArgumentException($"Unsupported zonal statistic '{raw}'. Allowed values: {string.Join(", ", _allowedZonalStatistics)}.", nameof(statistics));
            }

            if (seen.Add(normalized))
            {
                requestedStats.Add(normalized);
            }
        }

        if (requestedStats.Count == 0)
        {
            throw new ArgumentException("At least one statistic must be requested.", nameof(statistics));
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Require an existing source raster before running the zonal query so missing
        // rasters surface as a first-class error rather than silently becoming zero-count
        // rows per zone. See IRasterStore.ComputeZonalStatisticsAsync contract.
        int rasterSrid;
        await using (var sridCommand = connection.CreateCommand())
        {
            sridCommand.CommandText = $"""
                SELECT ST_SRID(raster)
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
                """;
            AddParameter(sridCommand, "@layerId", layerId);
            AddParameter(sridCommand, "@rasterId", rasterId);

            var sridScalar = await sridCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (sridScalar is null || sridScalar is DBNull)
            {
                PostgresRasterLog.RasterNotFound(_logger, layerId, rasterId);
                throw new InvalidOperationException(
                    $"Raster {rasterId} was not found in layer {layerId}.");
            }

            rasterSrid = Convert.ToInt32(sridScalar, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Reject rasters with an unknown CRS up front: ST_Transform(geometry, 0) aborts
        // the whole query in PostGIS, so surface this as a controlled error instead.
        if (rasterSrid <= 0)
        {
            throw new InvalidOperationException(
                $"Raster {rasterId} in layer {layerId} has unknown SRID ({rasterSrid}); assign a CRS before computing zonal statistics.");
        }

        await using var command = connection.CreateCommand();
        // Normalize each zone geometry to the raster SRID up front (reject zones with
        // unknown SRIDs explicitly) so ST_Intersects and ST_Clip operate on matched
        // coordinate systems. CROSS JOIN guarantees one row per zone only after the
        // source raster existence check above.
        command.CommandText = $"""
            WITH src AS (
                SELECT raster, ST_SRID(raster) AS raster_srid
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            ),
            zones AS (
                SELECT objectid,
                       CASE
                           WHEN ST_SRID(geometry) = @rasterSrid THEN geometry
                           ELSE ST_Transform(geometry, @rasterSrid)
                       END AS geometry
                FROM {_featuresTable}
                WHERE layer_id = @zonesLayerId
                  AND geometry IS NOT NULL
                  AND ST_SRID(geometry) > 0
            ),
            zone_stats AS (
                SELECT z.objectid AS zone_id,
                       CASE WHEN NOT ST_Intersects(r.raster, z.geometry)
                            THEN NULL
                            ELSE ST_SummaryStats(ST_Clip(r.raster, z.geometry, TRUE), @band)
                       END AS stats
                FROM zones z
                CROSS JOIN src r
            )
            SELECT zone_id,
                   COALESCE((stats).count, 0)::bigint AS pixel_count,
                   (stats).sum        AS sum_val,
                   (stats).mean       AS mean_val,
                   (stats).min        AS min_val,
                   (stats).max        AS max_val,
                   (stats).stddev     AS stddev_val
            FROM zone_stats
            ORDER BY zone_id
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);
        AddParameter(command, "@zonesLayerId", zonesLayerId);
        AddParameter(command, "@band", band);
        AddParameter(command, "@rasterSrid", rasterSrid);

        var rows = new List<RasterZonalStatisticsRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var zoneId = reader.GetInt64(0);
            var pixelCount = reader.GetInt64(1);
            var sumVal = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
            var meanVal = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3);
            var minVal = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4);
            var maxVal = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5);
            var stddevVal = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6);

            var stats = new Dictionary<string, double?>(requestedStats.Count, StringComparer.Ordinal);
            foreach (var name in requestedStats)
            {
                stats[name] = name switch
                {
                    "count" => pixelCount,
                    "sum" => sumVal,
                    "mean" => meanVal,
                    "min" => minVal,
                    "max" => maxVal,
                    "stddev" => stddevVal,
                    "variance" => stddevVal.HasValue ? stddevVal.Value * stddevVal.Value : (double?)null,
                    _ => null,
                };
            }

            rows.Add(new RasterZonalStatisticsRow
            {
                ZoneFeatureId = zoneId,
                Band = band,
                PixelCount = pixelCount,
                Stats = stats,
            });
        }

        PostgresRasterLog.ZonalStatisticsComputed(_logger, layerId, rasterId, zonesLayerId, band, rows.Count);
        return rows.ToArray();
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

    // GDAL driver availability is static for the PostGIS process lifetime.
    // Cache per driver name to avoid querying ST_GDALDrivers() on every export.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _gdalDriverCache = new();

    /// <summary>
    /// Resolves the effective GDAL format and creation options for COG output.
    /// Falls back to GTiff with COG-compatible options if the native COG driver is unavailable.
    /// </summary>
    private async Task<(string EffectiveFormat, string CreationOptionsClause)> ResolveCogOptionsAsync(
        DbConnection connection,
        int blockSize,
        int layerId,
        long rasterId,
        bool includeOverviewResampling,
        CancellationToken cancellationToken)
    {
        var hasCogDriver = await CheckGdalDriverAsync(connection, "COG", cancellationToken).ConfigureAwait(false);
        if (hasCogDriver)
        {
            var options = $"'COMPRESS=DEFLATE', 'BLOCKSIZE={blockSize}'";
            if (includeOverviewResampling)
            {
                options += ", 'OVERVIEW_RESAMPLING=NEAREST'";
            }

            return ("COG", $", ARRAY[{options}]");
        }

        PostgresRasterLog.CogDriverFallback(_logger, layerId, rasterId);
        return ("GTiff", $", ARRAY['TILED=YES', 'COMPRESS=DEFLATE', 'BLOCKXSIZE={blockSize}', 'BLOCKYSIZE={blockSize}']");
    }

    private static async Task<bool> CheckGdalDriverAsync(
        DbConnection connection,
        string driverName,
        CancellationToken cancellationToken)
    {
        if (_gdalDriverCache.TryGetValue(driverName, out var cached))
        {
            return cached;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM ST_GDALDrivers() WHERE short_name = @driver LIMIT 1";
        AddParameter(cmd, "@driver", driverName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var available = result != null;
        _gdalDriverCache.TryAdd(driverName, available);
        return available;
    }

    private static void AddParameter(DbCommand command, string name, object value)
        => command.AddParameter(name, value);
}
