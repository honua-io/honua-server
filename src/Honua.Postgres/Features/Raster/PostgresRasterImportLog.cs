// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Structured logging for PostgreSQL raster import operations.
/// Event ID range: 7820-7839 (adjacent to existing raster events 7800-7819).
/// </summary>
internal static partial class PostgresRasterImportLog
{
    [LoggerMessage(
        EventId = 7820,
        Level = LogLevel.Information,
        Message = "Starting raster import for layer {LayerId}: file={FileName}, format={Format}")]
    public static partial void ImportStarted(ILogger logger, int layerId, string fileName, string format);

    [LoggerMessage(
        EventId = 7821,
        Level = LogLevel.Information,
        Message = "Raster ingested: id={RasterId}, {Width}x{Height}, {BandCount} bands, SRID={Srid}")]
    public static partial void RasterIngested(ILogger logger, long rasterId, int width, int height, int bandCount, int srid);

    [LoggerMessage(
        EventId = 7822,
        Level = LogLevel.Information,
        Message = "Statistics computed for raster {RasterId}: {BandCount} bands")]
    public static partial void StatisticsComputed(ILogger logger, long rasterId, int bandCount);

    [LoggerMessage(
        EventId = 7823,
        Level = LogLevel.Information,
        Message = "Tiles pre-generated for raster {RasterId}: {TileCount} tiles across {ZoomLevels} zoom levels")]
    public static partial void TilesPreGenerated(ILogger logger, long rasterId, int tileCount, int zoomLevels);

    [LoggerMessage(
        EventId = 7824,
        Level = LogLevel.Information,
        Message = "Raster import completed: id={RasterId}, layer={LayerId}, duration={DurationSeconds:F2}s")]
    public static partial void ImportCompleted(ILogger logger, long rasterId, int layerId, double durationSeconds);

    [LoggerMessage(
        EventId = 7825,
        Level = LogLevel.Error,
        Message = "Raster import failed for layer {LayerId}: file={FileName}")]
    public static partial void ImportFailed(ILogger logger, Exception ex, int layerId, string fileName);
}
