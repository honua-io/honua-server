// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.TileCachePackage.Services;

/// <summary>
/// Minimal projection of an Esri cache <c>root.json</c> descriptor (Compact Cache V2,
/// <c>.tpkx</c>/<c>.vtpk</c>). Only the fields Honua needs to bind a served tileset
/// are modelled; the documented schema is published at
/// https://github.com/Esri/tile-package-spec.
/// </summary>
internal sealed record EsriRootJson
{
    /// <summary>Tile data format string, e.g. <c>PNG</c>, <c>JPEG</c>, <c>PBF</c>.</summary>
    [JsonPropertyName("tileInfo")]
    public EsriTileInfo? TileInfo { get; init; }

    /// <summary>Storage info (storage format + packet size).</summary>
    [JsonPropertyName("storageInfo")]
    public EsriStorageInfo? StorageInfo { get; init; }

    /// <summary>Relative path to the tile bundles, e.g. <c>./tile</c>.</summary>
    [JsonPropertyName("tileBundlesPath")]
    public string? TileBundlesPath { get; init; }

    /// <summary>Optional service/item name carried into the tileset title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Capability string; vector caches advertise <c>TilesOnly</c> with PBF tiles.</summary>
    [JsonPropertyName("capabilities")]
    public string? Capabilities { get; init; }
}

/// <summary>Tile info block of <c>root.json</c>.</summary>
internal sealed record EsriTileInfo
{
    /// <summary>Tile width in pixels.</summary>
    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    /// <summary>Tile height in pixels.</summary>
    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    /// <summary>Tile format, e.g. <c>PNG</c>, <c>JPEG</c>, <c>MIXED</c>, <c>PBF</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Spatial reference of the tiling scheme.</summary>
    [JsonPropertyName("spatialReference")]
    public EsriSpatialReference? SpatialReference { get; init; }

    /// <summary>Levels of detail.</summary>
    [JsonPropertyName("lods")]
    public EsriLod[]? Lods { get; init; }
}

/// <summary>Spatial reference block.</summary>
internal sealed record EsriSpatialReference
{
    /// <summary>Well-known id.</summary>
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    /// <summary>Latest well-known id (preferred when present).</summary>
    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

/// <summary>Level-of-detail entry.</summary>
internal sealed record EsriLod
{
    /// <summary>Level id.</summary>
    [JsonPropertyName("level")]
    public int Level { get; init; }
}

/// <summary>Storage info block.</summary>
internal sealed record EsriStorageInfo
{
    /// <summary>Storage format, e.g. <c>esriMapCacheStorageModeCompactV2</c>.</summary>
    [JsonPropertyName("storageFormat")]
    public string? StorageFormat { get; init; }

    /// <summary>Packet size (128 for Compact Cache V2).</summary>
    [JsonPropertyName("packetSize")]
    public int? PacketSize { get; init; }
}

/// <summary>
/// Source-generated JSON context for <c>root.json</c> parsing. Keeps the package
/// reader AOT-safe (no runtime reflection-based deserialization).
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EsriRootJson))]
internal sealed partial class TileCachePackageJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
