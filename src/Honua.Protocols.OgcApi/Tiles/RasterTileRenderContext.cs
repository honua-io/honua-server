// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.Tiles;

namespace Honua.Protocols.Ogc.Api.Tiles;

/// <summary>
/// Immutable bundle of the shared render inputs (tile envelope + SRID, tile limits, tile options,
/// and the active telemetry span) threaded through the OGC API Tiles raster (PNG) render paths.
/// Collapsing these co-travelling values into one carrier keeps the single-layer and dataset
/// raster-tile render methods within the endpoint parameter budget without changing behavior.
/// </summary>
/// <param name="Bounds">Tile envelope in the gridset CRS used to build the spatial filter and rasterize.</param>
/// <param name="FilterSrid">SRID of <paramref name="Bounds"/> and the feature-query output projection.</param>
/// <param name="TileLimits">Server-wide tile limits (per-tile feature budget).</param>
/// <param name="TileOptions">Tile options controlling cache-control max-age.</param>
/// <param name="Activity">Active tile-generation telemetry span, or <see langword="null"/>.</param>
internal sealed record RasterTileRenderContext(
    TileBounds Bounds,
    int FilterSrid,
    TileLimits TileLimits,
    TileOptions TileOptions,
    Activity? Activity);
