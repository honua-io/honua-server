// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.VectorTileServer.Models;

internal sealed class VectorTileServerMetadataResponse
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("capabilities")]
    public string Capabilities { get; init; } = "TilesOnly";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "indexedVector";

    [JsonPropertyName("tiles")]
    public string[] Tiles { get; init; } = ["tile/{z}/{y}/{x}.pbf"];

    [JsonPropertyName("exportTilesAllowed")]
    public bool ExportTilesAllowed { get; init; }

    [JsonPropertyName("minLOD")]
    public int MinLod { get; init; }

    [JsonPropertyName("maxLOD")]
    public int MaxLod { get; init; }

    [JsonPropertyName("defaultStyles")]
    public string DefaultStyles { get; init; } = "resources/styles";

    [JsonPropertyName("tileMap")]
    public string TileMap { get; init; } = "tilemap";

    [JsonPropertyName("tileInfo")]
    public VectorTileInfo? TileInfo { get; init; }

    [JsonPropertyName("fullExtent")]
    public VectorTileExtent? FullExtent { get; init; }

    [JsonPropertyName("initialExtent")]
    public VectorTileExtent? InitialExtent { get; init; }
}

internal sealed class VectorTileInfo
{
    [JsonPropertyName("rows")]
    public int Rows { get; init; } = 512;

    [JsonPropertyName("cols")]
    public int Cols { get; init; } = 512;

    [JsonPropertyName("dpi")]
    public int Dpi { get; init; } = 96;

    [JsonPropertyName("format")]
    public string Format { get; init; } = "pbf";

    [JsonPropertyName("origin")]
    public VectorTileOrigin? Origin { get; init; }

    [JsonPropertyName("spatialReference")]
    public VectorTileSpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("lods")]
    public VectorTileLevelOfDetail[]? Lods { get; init; }
}

internal sealed class VectorTileOrigin
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }
}

internal sealed class VectorTileSpatialReference
{
    [JsonPropertyName("wkid")]
    public required int Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

internal sealed class VectorTileLevelOfDetail
{
    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("resolution")]
    public double Resolution { get; init; }

    [JsonPropertyName("scale")]
    public double Scale { get; init; }
}

internal sealed class VectorTileMapResponse
{
    [JsonPropertyName("adjusted")]
    public bool Adjusted { get; init; }

    [JsonPropertyName("location")]
    public required VectorTileMapLocation Location { get; init; }

    [JsonPropertyName("data")]
    public required int[] Data { get; init; }
}

internal sealed class VectorTileMapLocation
{
    [JsonPropertyName("left")]
    public required int Left { get; init; }

    [JsonPropertyName("top")]
    public required int Top { get; init; }

    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }
}

internal sealed class VectorTileExtent
{
    [JsonPropertyName("xmin")]
    public required double Xmin { get; init; }

    [JsonPropertyName("ymin")]
    public required double Ymin { get; init; }

    [JsonPropertyName("xmax")]
    public required double Xmax { get; init; }

    [JsonPropertyName("ymax")]
    public required double Ymax { get; init; }

    [JsonPropertyName("spatialReference")]
    public required VectorTileSpatialReference SpatialReference { get; init; }
}
