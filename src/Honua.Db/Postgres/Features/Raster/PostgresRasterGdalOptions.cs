// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

internal static class PostgresRasterGdalOptions
{
    private const int MinJpegQuality = 10;
    private const int MaxJpegQuality = 100;

    internal static int ClampJpegQuality(int quality)
        => Math.Clamp(quality, MinJpegQuality, MaxJpegQuality);
}
