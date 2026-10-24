// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.Raster;

internal static partial class PostgresTerrainLog
{
    [LoggerMessage(
        EventId = 29100,
        Level = LogLevel.Debug,
        Message = "Generated Terrain-RGB tile for layer {LayerId} z={Z} x={X} y={Y}; rasters={RasterCount}, bytes={ByteCount}, allNoData={AllNoData}")]
    public static partial void TerrainTileGenerated(
        ILogger logger,
        int layerId,
        int z,
        int x,
        int y,
        int rasterCount,
        int byteCount,
        bool allNoData);

    [LoggerMessage(
        EventId = 29101,
        Level = LogLevel.Debug,
        Message = "Terrain dataset metadata resolved for layer {LayerId}; rasters={RasterCount}, supported={Supported}")]
    public static partial void TerrainMetadataResolved(
        ILogger logger,
        int layerId,
        int rasterCount,
        bool supported);
}
