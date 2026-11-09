// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.TileCachePackage.Services;

internal sealed record GeoServicesRootJson
{
    [JsonPropertyName("tileInfo")]
    public GeoServicesTileInfo? TileInfo { get; init; }

    [JsonPropertyName("storageInfo")]
    public GeoServicesStorageInfo? StorageInfo { get; init; }

    [JsonPropertyName("tileBundlesPath")]
    public string? TileBundlesPath { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("capabilities")]
    public string? Capabilities { get; init; }
}

internal sealed record GeoServicesTileInfo
{
    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("spatialReference")]
    public SpatialReferenceJson? SpatialReference { get; init; }

    [JsonPropertyName("origin")]
    public GeoServicesPoint? Origin { get; init; }

    [JsonPropertyName("lods")]
    public GeoServicesLod[]? Lods { get; init; }
}

internal sealed record SpatialReferenceJson
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

internal sealed record GeoServicesLod
{
    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("resolution")]
    public double? Resolution { get; init; }

    [JsonPropertyName("scale")]
    public double? Scale { get; init; }
}

internal sealed record GeoServicesPoint
{
    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }
}

internal sealed record GeoServicesStorageInfo
{
    [JsonPropertyName("storageFormat")]
    public string? StorageFormat { get; init; }

    [JsonPropertyName("packetSize")]
    public int? PacketSize { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GeoServicesRootJson))]
[JsonSerializable(typeof(GeoServicesPoint))]
internal sealed partial class TileCachePackageJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
