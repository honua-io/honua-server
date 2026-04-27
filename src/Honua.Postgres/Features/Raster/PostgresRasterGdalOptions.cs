// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Postgres.Features.Raster;

internal static class PostgresRasterGdalOptions
{
    private const int MinJpegQuality = 10;
    private const int MaxJpegQuality = 100;

    internal static int ClampJpegQuality(int quality)
        => Math.Clamp(quality, MinJpegQuality, MaxJpegQuality);
}
