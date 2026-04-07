// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Structured logging for PostgreSQL raster store operations.
/// Event ID range: 7800-7899 (following the memory guidelines).
/// </summary>
internal static partial class PostgresRasterLog
{
    [LoggerMessage(
        EventId = 7800,
        Level = LogLevel.Debug,
        Message = "Raster not found for layer {LayerId}, raster {RasterId}")]
    public static partial void RasterNotFound(ILogger logger, int layerId, long rasterId);

    [LoggerMessage(
        EventId = 7801,
        Level = LogLevel.Debug,
        Message = "Retrieved raster info for layer {LayerId}, raster {RasterId}: {Width}x{Height}")]
    public static partial void RasterInfoRetrieved(ILogger logger, int layerId, long rasterId, int width, int height);

    [LoggerMessage(
        EventId = 7802,
        Level = LogLevel.Information,
        Message = "Exported image for layer {LayerId}, raster {RasterId}: {Width}x{Height}, {DataSize} bytes")]
    public static partial void ImageExported(ILogger logger, int layerId, long rasterId, int width, int height, int dataSize);

    [LoggerMessage(
        EventId = 7803,
        Level = LogLevel.Debug,
        Message = "Identified pixel value for layer {LayerId}, raster {RasterId} at ({X}, {Y}): HasData={HasData}, BandCount={BandCount}")]
    public static partial void PixelValueIdentified(ILogger logger, int layerId, long rasterId, double x, double y, bool hasData, int bandCount);

    [LoggerMessage(
        EventId = 7804,
        Level = LogLevel.Information,
        Message = "Generated tile for layer {LayerId}, raster {RasterId}: level={Level}, row={Row}, col={Col}, {DataSize} bytes")]
    public static partial void TileGenerated(ILogger logger, int layerId, long rasterId, int level, int row, int col, int dataSize);

    [LoggerMessage(
        EventId = 7805,
        Level = LogLevel.Debug,
        Message = "Calculated statistics for layer {LayerId}, raster {RasterId}: {BandCount} bands")]
    public static partial void StatisticsCalculated(ILogger logger, int layerId, long rasterId, int bandCount);

    [LoggerMessage(
        EventId = 7806,
        Level = LogLevel.Debug,
        Message = "Retrieved raster list for layer {LayerId}: {Count} rasters")]
    public static partial void RasterListRetrieved(ILogger logger, int layerId, int count);

    [LoggerMessage(
        EventId = 7807,
        Level = LogLevel.Warning,
        Message = "PostGIS raster extension not available. Raster operations may fail.")]
    public static partial void PostGisRasterExtensionMissing(ILogger logger);

    [LoggerMessage(
        EventId = 7808,
        Level = LogLevel.Error,
        Message = "Failed to process raster query for layer {LayerId}, raster {RasterId}")]
    public static partial void RasterQueryFailed(ILogger logger, Exception ex, int layerId, long rasterId);

    [LoggerMessage(
        EventId = 7809,
        Level = LogLevel.Warning,
        Message = "Raster operation may be slow due to missing spatial index on layer {LayerId}")]
    public static partial void MissingSpatialIndex(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 7810,
        Level = LogLevel.Information,
        Message = "Starting bulk raster import for layer {LayerId} with {FileCount} files")]
    public static partial void BulkRasterImportStarted(ILogger logger, int layerId, int fileCount);

    [LoggerMessage(
        EventId = 7811,
        Level = LogLevel.Information,
        Message = "Completed bulk raster import for layer {LayerId}: {SuccessCount} successful, {ErrorCount} errors")]
    public static partial void BulkRasterImportCompleted(ILogger logger, int layerId, int successCount, int errorCount);

    [LoggerMessage(
        EventId = 7812,
        Level = LogLevel.Warning,
        Message = "Raster operation warning in {MethodName}: {Detail}")]
    public static partial void RasterOperationWarning(ILogger logger, string methodName, string detail);

    [LoggerMessage(
        EventId = 7813,
        Level = LogLevel.Warning,
        Message = "COG GDAL driver not available for layer {LayerId}, raster {RasterId}. Falling back to GTiff with COG-compatible options (TILED=YES, COMPRESS=DEFLATE)")]
    public static partial void CogDriverFallback(ILogger logger, int layerId, long rasterId);

    [LoggerMessage(
        EventId = 7814,
        Level = LogLevel.Warning,
        Message = "Histogram computation failed for layer {LayerId}, raster {RasterId}, band {Band}")]
    public static partial void HistogramFailed(ILogger logger, Exception ex, int layerId, long rasterId, int band);

    [LoggerMessage(
        EventId = 7816,
        Level = LogLevel.Debug,
        Message = "Batched histogram query failed for layer {LayerId}, raster {RasterId}; falling back to per-band loop")]
    public static partial void HistogramBatchFallback(ILogger logger, Exception ex, int layerId, long rasterId);

    [LoggerMessage(
        EventId = 7815,
        Level = LogLevel.Debug,
        Message = "Catalog query for layer {LayerId}: returned={ReturnedCount}, total={TotalCount}, exceededTransferLimit={ExceededTransferLimit}")]
    public static partial void CatalogQueried(ILogger logger, int layerId, int returnedCount, long totalCount, bool exceededTransferLimit);
}
