// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.Tiles;

namespace Honua.Protocols.Ogc.Api.Tiles;

internal sealed record RasterTileRenderContext(
    TileBounds Bounds,
    int FilterSrid,
    TileLimits TileLimits,
    TileOptions TileOptions,
    Activity? Activity,
    string TileMatrixSetId);
