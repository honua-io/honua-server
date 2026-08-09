// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

internal static partial class PostgresElevationLog
{
    [LoggerMessage(
        EventId = 29200,
        Level = LogLevel.Debug,
        Message = "Elevation point query for layer {LayerId} at ({X}, {Y}) srid={Srid}; rasters={RasterCount}, noData={NoData}")]
    public static partial void ElevationPointQueried(
        ILogger logger,
        int layerId,
        double x,
        double y,
        int srid,
        int rasterCount,
        bool noData);

    [LoggerMessage(
        EventId = 29201,
        Level = LogLevel.Debug,
        Message = "Elevation profile for layer {LayerId} sampleCount={SampleCount} lineLengthMeters={LineLengthMeters}; rasters={RasterCount}, allNoData={AllNoData}")]
    public static partial void ElevationProfileQueried(
        ILogger logger,
        int layerId,
        int sampleCount,
        double lineLengthMeters,
        int rasterCount,
        bool allNoData);
}
