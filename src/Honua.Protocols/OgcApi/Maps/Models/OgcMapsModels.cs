// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Ogc.Api.Maps.Models;

public sealed class OgcMapsConformance
{
    [JsonPropertyName("conformsTo")]
    public required string[] ConformsTo { get; init; }
}

public sealed class OgcMapRequest
{
    [StringLength(500, ErrorMessage = "Collections parameter is too long")]
    public string? Collections { get; init; }

    [StringLength(100, ErrorMessage = "StyleId is too long")]
    [RegularExpression(@"^[a-zA-Z0-9_-]+$", ErrorMessage = "StyleId contains invalid characters")]
    public string? StyleId { get; init; }

    [StringLength(200, ErrorMessage = "CRS parameter is too long")]
    public string? Crs { get; init; }

    [RegularExpression(@"^-?\d+(\.\d+)?,-?\d+(\.\d+)?,-?\d+(\.\d+)?,-?\d+(\.\d+)?$",
        ErrorMessage = "Bbox must be in format: minX,minY,maxX,maxY")]
    [StringLength(100, ErrorMessage = "Bbox is too long")]
    public string? Bbox { get; init; }

    [FromQuery(Name = "bbox-crs")]
    [StringLength(200, ErrorMessage = "BboxCrs parameter is too long")]
    public string? BboxCrs { get; init; }

    [Range(1, 4096, ErrorMessage = "Width must be between 1 and 4096 pixels")]
    public int? Width { get; init; } = 256;

    [Range(1, 4096, ErrorMessage = "Height must be between 1 and 4096 pixels")]
    public int? Height { get; init; } = 256;

    [RegularExpression(@"^(png|jpeg|jpg|tiff|tif)$", ErrorMessage = "Format must be png, jpeg, jpg, tiff, or tif")]
    public string? F { get; init; }

    public bool? Transparent { get; init; } = true;

    [FromQuery(Name = "bgcolor")]
    [RegularExpression(@"^0x[0-9A-Fa-f]{6}$", ErrorMessage = "BackgroundColor must be in format 0xRRGGBB")]
    [StringLength(8, ErrorMessage = "BackgroundColor is too long")]
    public string? BackgroundColor { get; init; } = "0xFFFFFF";

    [StringLength(100, ErrorMessage = "Datetime parameter is too long")]
    public string? Datetime { get; init; }

    [Range(1, 100, ErrorMessage = "Quality must be between 1 and 100")]
    public int? Quality { get; init; }
}

public sealed class OgcLink
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("hreflang")]
    public string? HrefLang { get; init; }

    [JsonPropertyName("templated")]
    public bool? Templated { get; init; }
}

public sealed class MapResponse
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("extent")]
    public MapExtent? Extent { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("links")]
    public required OgcLink[] Links { get; init; }
}

public sealed class MapExtent
{
    [JsonPropertyName("spatial")]
    public SpatialExtent? Spatial { get; init; }

    [JsonPropertyName("temporal")]
    public TemporalExtent? Temporal { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }
}

public sealed class SpatialExtent
{
    [JsonPropertyName("bbox")]
    public required double[][] Bbox { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; } = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}

public sealed class TemporalExtent
{
    [JsonPropertyName("interval")]
    public required string[][] Interval { get; init; }

    [JsonPropertyName("trs")]
    public string? Trs { get; init; } = "http://www.opengis.net/def/uom/ISO-8601/0/Gregorian";
}

public sealed class MapCollectionInfo
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("extent")]
    public MapExtent? Extent { get; init; }

    [JsonPropertyName("itemType")]
    public string? ItemType { get; init; }

    [JsonPropertyName("crs")]
    public string[]? Crs { get; init; }

    [JsonPropertyName("storageCrs")]
    public string? StorageCrs { get; init; }

    [JsonPropertyName("links")]
    public OgcLink[]? Links { get; init; }
}

public sealed class MapStyle
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("keywords")]
    public string[]? Keywords { get; init; }

    [JsonPropertyName("links")]
    public OgcLink[]? Links { get; init; }
}

public sealed class DatasetMap
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("extent")]
    public MapExtent? Extent { get; init; }

    [JsonPropertyName("crs")]
    public string[]? Crs { get; init; }

    [JsonPropertyName("collections")]
    public required string[] Collections { get; init; }

    [JsonPropertyName("links")]
    public required OgcLink[] Links { get; init; }
}

public sealed class TileSetsList
{
    [JsonPropertyName("tilesets")]
    public required TileSet[] Tilesets { get; init; }

    [JsonPropertyName("links")]
    public required OgcLink[] Links { get; init; }
}

public sealed class TileSet
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("dataType")]
    public string DataType { get; init; } = "map";

    [JsonPropertyName("crs")]
    public required string Crs { get; init; }

    [JsonPropertyName("tileMatrixSetId")]
    public string? TileMatrixSetId { get; init; }

    [JsonPropertyName("tileMatrixSetURI")]
    public required string TileMatrixSetUri { get; init; }

    [JsonPropertyName("links")]
    public required OgcLink[] Links { get; init; }
}
