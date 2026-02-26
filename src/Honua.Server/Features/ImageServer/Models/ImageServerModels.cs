// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.ImageServer.Models;

/// <summary>
/// Esri Image Server service metadata response.
/// </summary>
public sealed class ImageServerServiceInfo
{
    [JsonPropertyName("currentVersion")]
    public required double CurrentVersion { get; init; }

    [JsonPropertyName("serviceDescription")]
    public required string ServiceDescription { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("extent")]
    public required ImageServerExtent Extent { get; init; }

    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }

    [JsonPropertyName("pixelSizeX")]
    public required double PixelSizeX { get; init; }

    [JsonPropertyName("pixelSizeY")]
    public required double PixelSizeY { get; init; }

    [JsonPropertyName("bandCount")]
    public required int BandCount { get; init; }

    [JsonPropertyName("pixelType")]
    public required string PixelType { get; init; }

    [JsonPropertyName("minPixelSize")]
    public required double MinPixelSize { get; init; }

    [JsonPropertyName("maxPixelSize")]
    public required double MaxPixelSize { get; init; }

    [JsonPropertyName("copyrightText")]
    public string? CopyrightText { get; init; }

    [JsonPropertyName("serviceDataType")]
    public required string ServiceDataType { get; init; } = "esriImageServiceDataTypeGeneric";

    [JsonPropertyName("minValues")]
    public double[]? MinValues { get; init; }

    [JsonPropertyName("maxValues")]
    public double[]? MaxValues { get; init; }

    [JsonPropertyName("meanValues")]
    public double[]? MeanValues { get; init; }

    [JsonPropertyName("stdvValues")]
    public double[]? StdvValues { get; init; }

    [JsonPropertyName("objectIdField")]
    public string? ObjectIdField { get; init; }

    [JsonPropertyName("fields")]
    public Field[]? Fields { get; init; }

    [JsonPropertyName("capabilities")]
    public required string Capabilities { get; init; } = "Catalog,Image,Metadata,Pixels";

    [JsonPropertyName("defaultMosaicMethod")]
    public string DefaultMosaicMethod { get; init; } = "esriMosaicNorthwest";

    [JsonPropertyName("allowedMosaicMethods")]
    public string[] AllowedMosaicMethods { get; init; } = ["esriMosaicNorthwest", "esriMosaicCenter"];

    [JsonPropertyName("sortField")]
    public string? SortField { get; init; }

    [JsonPropertyName("sortValue")]
    public string? SortValue { get; init; }

    [JsonPropertyName("mosaicOperator")]
    public string MosaicOperator { get; init; } = "First";

    [JsonPropertyName("defaultCompressionQuality")]
    public int DefaultCompressionQuality { get; init; } = 75;

    [JsonPropertyName("defaultResamplingMethod")]
    public string DefaultResamplingMethod { get; init; } = "Bilinear";

    [JsonPropertyName("maxImageHeight")]
    public int MaxImageHeight { get; init; } = 4100;

    [JsonPropertyName("maxImageWidth")]
    public int MaxImageWidth { get; init; } = 15000;

    [JsonPropertyName("maxRecordCount")]
    public int MaxRecordCount { get; init; } = 1000;

    [JsonPropertyName("maxDownloadImageCount")]
    public int MaxDownloadImageCount { get; init; } = 20;

    [JsonPropertyName("maxMosaicImageCount")]
    public int MaxMosaicImageCount { get; init; } = 2000;

    [JsonPropertyName("singleFusedMapCache")]
    public bool SingleFusedMapCache { get; init; } = false;

    [JsonPropertyName("tileInfo")]
    public TileInfo? TileInfo { get; init; }

    [JsonPropertyName("cacheType")]
    public string? CacheType { get; init; }

    [JsonPropertyName("allowRasterFunction")]
    public bool AllowRasterFunction { get; init; } = false;

    [JsonPropertyName("rasterFunctionInfos")]
    public RasterFunctionInfo[]? RasterFunctionInfos { get; init; }

    [JsonPropertyName("rasterTypeInfos")]
    public RasterTypeInfo[]? RasterTypeInfos { get; init; }

    [JsonPropertyName("mensurationCapabilities")]
    public string? MensurationCapabilities { get; init; }

    [JsonPropertyName("hasHistograms")]
    public bool HasHistograms { get; init; } = false;

    [JsonPropertyName("hasColormap")]
    public bool HasColormap { get; init; } = false;

    [JsonPropertyName("hasRasterAttributeTable")]
    public bool HasRasterAttributeTable { get; init; } = false;

    [JsonPropertyName("spatialReferenceServiceUrl")]
    public string? SpatialReferenceServiceUrl { get; init; }
}

/// <summary>
/// Spatial reference information.
/// </summary>
public sealed class SpatialReference
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }

    [JsonPropertyName("wkt")]
    public string? Wkt { get; init; }
}

/// <summary>
/// Spatial extent for image services.
/// </summary>
public sealed class ImageServerExtent
{
    [JsonPropertyName("xmin")]
    public required double XMin { get; init; }

    [JsonPropertyName("ymin")]
    public required double YMin { get; init; }

    [JsonPropertyName("xmax")]
    public required double XMax { get; init; }

    [JsonPropertyName("ymax")]
    public required double YMax { get; init; }

    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }
}

/// <summary>
/// Field definition for image services.
/// </summary>
public sealed class Field
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("domain")]
    public object? Domain { get; init; }

    [JsonPropertyName("editable")]
    public bool Editable { get; init; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; }

    [JsonPropertyName("defaultValue")]
    public object? DefaultValue { get; init; }

    [JsonPropertyName("modelName")]
    public string? ModelName { get; init; }
}

/// <summary>
/// Tile information for cached services.
/// </summary>
public sealed class TileInfo
{
    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    [JsonPropertyName("dpi")]
    public required int Dpi { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("compressionQuality")]
    public int? CompressionQuality { get; init; }

    [JsonPropertyName("origin")]
    public required Point Origin { get; init; }

    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }

    [JsonPropertyName("lods")]
    public required LevelOfDetail[] Lods { get; init; }
}

/// <summary>
/// Point coordinates.
/// </summary>
public sealed class Point
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }
}

/// <summary>
/// Level of detail for tiling.
/// </summary>
public sealed class LevelOfDetail
{
    [JsonPropertyName("level")]
    public required int Level { get; init; }

    [JsonPropertyName("resolution")]
    public required double Resolution { get; init; }

    [JsonPropertyName("scale")]
    public required double Scale { get; init; }
}

/// <summary>
/// Raster function information.
/// </summary>
public sealed class RasterFunctionInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("help")]
    public string? Help { get; init; }
}

/// <summary>
/// Raster type information.
/// </summary>
public sealed class RasterTypeInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("help")]
    public string? Help { get; init; }
}

/// <summary>
/// Export image response.
/// </summary>
public sealed class ExportImageResponse
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }

    [JsonPropertyName("extent")]
    public required ImageServerExtent Extent { get; init; }

    [JsonPropertyName("scale")]
    public double? Scale { get; init; }
}

/// <summary>
/// Identify response with pixel values.
/// </summary>
public sealed class IdentifyResponse
{
    [JsonPropertyName("objectId")]
    public long? ObjectId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("location")]
    public required Point Location { get; init; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object?>? Properties { get; init; }

    [JsonPropertyName("catalogItems")]
    public CatalogItem[]? CatalogItems { get; init; }
}

/// <summary>
/// Catalog item for identify operations.
/// </summary>
public sealed class CatalogItem
{
    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("footprint")]
    public object? Footprint { get; init; }
}

/// <summary>
/// Export image request parameters.
/// </summary>
public sealed class ExportImageRequest
{
    [RegularExpression(@"^-?\d+(\.\d+)?,-?\d+(\.\d+)?,-?\d+(\.\d+)?,-?\d+(\.\d+)?$",
        ErrorMessage = "Bbox must be in format: minX,minY,maxX,maxY")]
    [StringLength(100, ErrorMessage = "Bbox is too long")]
    public string? Bbox { get; init; }

    [Range(1, 4096, ErrorMessage = "Size must be between 1 and 4096")]
    public int? Size { get; init; }

    [RegularExpression(@"^\d{1,6}$", ErrorMessage = "ImageSr must be a valid SRID")]
    public string? ImageSr { get; init; }

    [RegularExpression(@"^\d{1,6}$", ErrorMessage = "BboxSr must be a valid SRID")]
    public string? BboxSr { get; init; }

    [RegularExpression(@"^(png|jpg|jpeg|tiff|tif)$",
        ErrorMessage = "Format must be png, jpg, jpeg, tiff, or tif")]
    public string? Format { get; init; } = "png";

    [Range(0, 255, ErrorMessage = "PixelType must be between 0 and 255")]
    public int? PixelType { get; init; }

    [StringLength(100, ErrorMessage = "NoData value is too long")]
    public string? NoData { get; init; }

    [StringLength(50, ErrorMessage = "NoDataInterpretation is too long")]
    public string? NoDataInterpretation { get; init; } = "esriNoDataMatchAny";

    [StringLength(50, ErrorMessage = "Interpolation value is too long")]
    public string? Interpolation { get; init; } = "RSP_BilinearInterpolation";

    [Range(0, 100, ErrorMessage = "Compression must be between 0 and 100")]
    public int? Compression { get; init; }

    [Range(1, 100, ErrorMessage = "CompressionQuality must be between 1 and 100")]
    public int? CompressionQuality { get; init; } = 75;

    [StringLength(100, ErrorMessage = "BandIds is too long")]
    public string? BandIds { get; init; }

    [StringLength(1000, ErrorMessage = "MosaicRule is too long")]
    public string? MosaicRule { get; init; }

    [StringLength(1000, ErrorMessage = "RenderingRule is too long")]
    public string? RenderingRule { get; init; }

    [RegularExpression(@"^(json|pjson)$", ErrorMessage = "Format parameter must be 'json' or 'pjson'")]
    public string? F { get; init; } = "json";
}

/// <summary>
/// Identify request parameters.
/// </summary>
public sealed class IdentifyRequest
{
    [Required(ErrorMessage = "Geometry is required")]
    [StringLength(1000, ErrorMessage = "Geometry is too long")]
    public required string Geometry { get; init; }

    [StringLength(50, ErrorMessage = "GeometryType is too long")]
    public string? GeometryType { get; init; } = "esriGeometryPoint";

    [RegularExpression(@"^\d{1,6}$", ErrorMessage = "Sr must be a valid SRID")]
    public string? Sr { get; init; }

    [StringLength(1000, ErrorMessage = "MosaicRule is too long")]
    public string? MosaicRule { get; init; }

    [StringLength(1000, ErrorMessage = "RenderingRule is too long")]
    public string? RenderingRule { get; init; }

    [Range(1, 1000, ErrorMessage = "PixelSize must be between 1 and 1000")]
    public int? PixelSize { get; init; }

    [StringLength(100, ErrorMessage = "Time is too long")]
    public string? Time { get; init; }

    public bool? ReturnGeometry { get; init; } = true;

    public bool? ReturnCatalogItems { get; init; } = false;

    [RegularExpression(@"^(json|pjson)$", ErrorMessage = "Format parameter must be 'json' or 'pjson'")]
    public string? F { get; init; } = "json";
}
