// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct RasterIdentifyRendering(
    RasterStretch? Stretch,
    RasterColormap? Colormap,
    RasterClipRegion? Clip,
    RasterBandArithmetic? BandArithmetic = null,
    RasterTerrainFunction? Terrain = null)
{
    public bool HasRendering =>
        Stretch is not null
        || Colormap is not null
        || Clip is not null
        || BandArithmetic is not null
        || Terrain is not null;
}
