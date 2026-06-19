// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.VectorTileServer.Models;

/// <summary>
/// Response for the GeoServices VectorTileServer service metadata endpoint
/// (<c>GET/POST /rest/services/{serviceId}/VectorTileServer</c>). The shape mirrors the
/// Esri VectorTileServer service descriptor that ArcGIS clients hydrate when adding a
/// hosted vector tile layer: a <c>tiles</c> template, a WebMercatorQuad <see cref="VectorTileInfo"/>
/// descriptor, and the tileMap / styles / capabilities resource pointers. This is a thin
/// metadata adapter — Honua does not host an Esri-format vector tile cache, so the tile,
/// resources, and tileMap routes are stubbed in the foundation and filled in by follow-up
/// tickets (#1778 / #1779 / #1781).
/// </summary>
internal sealed class VectorTileServerMetadataResponse
{
    /// <summary>ArcGIS service version number.</summary>
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.81;

    /// <summary>Service name (the GeoServices service identifier the route resolves).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Capabilities advertised by the service. Tile-only for the metadata foundation.</summary>
    [JsonPropertyName("capabilities")]
    public string Capabilities { get; init; } = "TilesOnly";

    /// <summary>
    /// Vector-tile content type. ArcGIS uses <c>indexedVector</c> for hosted vector tile layers.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "indexedVector";

    /// <summary>Tile URL templates relative to the service root.</summary>
    [JsonPropertyName("tiles")]
    public string[] Tiles { get; init; } = ["tile/{z}/{y}/{x}.pbf"];

    /// <summary>Exported tiles are not supported by the metadata foundation.</summary>
    [JsonPropertyName("exportTilesAllowed")]
    public bool ExportTilesAllowed { get; init; }

    /// <summary>Lowest level of detail served by the tiling scheme.</summary>
    [JsonPropertyName("minLOD")]
    public int MinLod { get; init; }

    /// <summary>Highest level of detail served by the tiling scheme.</summary>
    [JsonPropertyName("maxLOD")]
    public int MaxLod { get; init; }

    /// <summary>Relative path of the default vector tile styles resource.</summary>
    [JsonPropertyName("defaultStyles")]
    public string DefaultStyles { get; init; } = "resources/styles";

    /// <summary>Relative path of the tile map (sparse tile index) resource.</summary>
    [JsonPropertyName("tileMap")]
    public string TileMap { get; init; } = "tilemap";

    /// <summary>WebMercatorQuad tiling-scheme descriptor.</summary>
    [JsonPropertyName("tileInfo")]
    public VectorTileInfo? TileInfo { get; init; }

    /// <summary>The full spatial extent of the service.</summary>
    [JsonPropertyName("fullExtent")]
    public VectorTileExtent? FullExtent { get; init; }

    /// <summary>The initial display extent of the service.</summary>
    [JsonPropertyName("initialExtent")]
    public VectorTileExtent? InitialExtent { get; init; }
}

/// <summary>
/// WebMercatorQuad tiling-scheme descriptor for a VectorTileServer service. Vector tiles use
/// 512-pixel rows/cols and the <c>pbf</c> (Mapbox Vector Tile) format, unlike the 256-pixel
/// PNG raster tiling scheme advertised by MapServer / ImageServer.
/// </summary>
internal sealed class VectorTileInfo
{
    /// <summary>Number of logical rows per tile (512 for the vector tiling scheme).</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; } = 512;

    /// <summary>Number of logical columns per tile (512 for the vector tiling scheme).</summary>
    [JsonPropertyName("cols")]
    public int Cols { get; init; } = 512;

    /// <summary>Logical DPI advertised for the tiling scheme.</summary>
    [JsonPropertyName("dpi")]
    public int Dpi { get; init; } = 96;

    /// <summary>Tile payload format. Vector tiles are protobuf-encoded MVT.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = "pbf";

    /// <summary>Tile origin point (top-left of the world extent) in map coordinates.</summary>
    [JsonPropertyName("origin")]
    public VectorTileOrigin? Origin { get; init; }

    /// <summary>Spatial reference of the tiling scheme.</summary>
    [JsonPropertyName("spatialReference")]
    public VectorTileSpatialReference? SpatialReference { get; init; }

    /// <summary>Levels of detail (zoom levels) available in the tiling scheme.</summary>
    [JsonPropertyName("lods")]
    public VectorTileLevelOfDetail[]? Lods { get; init; }
}

/// <summary>Tile origin point coordinates for a vector tiling scheme.</summary>
internal sealed class VectorTileOrigin
{
    /// <summary>X coordinate of the tile origin.</summary>
    [JsonPropertyName("x")]
    public double X { get; init; }

    /// <summary>Y coordinate of the tile origin.</summary>
    [JsonPropertyName("y")]
    public double Y { get; init; }
}

/// <summary>Spatial reference information for a VectorTileServer response.</summary>
internal sealed class VectorTileSpatialReference
{
    /// <summary>Well-Known ID (EPSG code) of the spatial reference.</summary>
    [JsonPropertyName("wkid")]
    public required int Wkid { get; init; }

    /// <summary>Latest Well-Known ID (for newer EPSG codes).</summary>
    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

/// <summary>Describes a single level of detail (zoom level) in the vector tiling scheme.</summary>
internal sealed class VectorTileLevelOfDetail
{
    /// <summary>Zoom level number.</summary>
    [JsonPropertyName("level")]
    public int Level { get; init; }

    /// <summary>Map resolution in units per pixel at this zoom level.</summary>
    [JsonPropertyName("resolution")]
    public double Resolution { get; init; }

    /// <summary>Scale denominator at this zoom level.</summary>
    [JsonPropertyName("scale")]
    public double Scale { get; init; }
}

/// <summary>
/// Response for the VectorTileServer tileMap endpoint
/// (<c>GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dim}/{dim}</c>).
/// The shape mirrors the Esri tileMap availability block: a <c>location</c> describing the
/// requested tile window and a row-major <c>data</c> array of <c>1</c> (tile in range) / <c>0</c>
/// (tile outside the LOD/coordinate bounds) flags.
/// </summary>
internal sealed class VectorTileMapResponse
{
    /// <summary>
    /// Whether the returned <see cref="Location"/> was adjusted (clamped) from the requested
    /// window. Honua returns the requested window verbatim, so this is always <see langword="false"/>.
    /// </summary>
    [JsonPropertyName("adjusted")]
    public bool Adjusted { get; init; }

    /// <summary>The tile window the availability block covers.</summary>
    [JsonPropertyName("location")]
    public required VectorTileMapLocation Location { get; init; }

    /// <summary>
    /// Row-major availability flags for the window (<c>Height * Width</c> entries): <c>1</c>
    /// when the tile is inside the LOD/coordinate bounds, <c>0</c> otherwise.
    /// </summary>
    [JsonPropertyName("data")]
    public required int[] Data { get; init; }
}

/// <summary>Describes the tile window (top-left origin and size, in tiles) of a tileMap block.</summary>
internal sealed class VectorTileMapLocation
{
    /// <summary>Column index (tile X) of the top-left tile in the window.</summary>
    [JsonPropertyName("left")]
    public required int Left { get; init; }

    /// <summary>Row index (tile Y) of the top-left tile in the window.</summary>
    [JsonPropertyName("top")]
    public required int Top { get; init; }

    /// <summary>Window width, in tiles.</summary>
    [JsonPropertyName("width")]
    public required int Width { get; init; }

    /// <summary>Window height, in tiles.</summary>
    [JsonPropertyName("height")]
    public required int Height { get; init; }
}

/// <summary>Esri spatial extent for a VectorTileServer response.</summary>
internal sealed class VectorTileExtent
{
    /// <summary>Minimum X coordinate.</summary>
    [JsonPropertyName("xmin")]
    public required double Xmin { get; init; }

    /// <summary>Minimum Y coordinate.</summary>
    [JsonPropertyName("ymin")]
    public required double Ymin { get; init; }

    /// <summary>Maximum X coordinate.</summary>
    [JsonPropertyName("xmax")]
    public required double Xmax { get; init; }

    /// <summary>Maximum Y coordinate.</summary>
    [JsonPropertyName("ymax")]
    public required double Ymax { get; init; }

    /// <summary>Spatial reference of the extent.</summary>
    [JsonPropertyName("spatialReference")]
    public required VectorTileSpatialReference SpatialReference { get; init; }
}
