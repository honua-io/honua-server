// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct RasterTileWindow
{
    public required double MinX { get; init; }

    public required double MinY { get; init; }

    public required double MaxX { get; init; }

    public required double MaxY { get; init; }

    public required int Srid { get; init; }

    public int TileWidth { get; init; }

    public int TileHeight { get; init; }

    public RasterTileWindow()
    {
        TileWidth = 256;
        TileHeight = 256;
    }
}
