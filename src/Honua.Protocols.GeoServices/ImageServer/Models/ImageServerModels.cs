// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

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

    /// <summary>
    /// Full geographic extent of the image service. The native .NET ArcGIS Runtime
    /// <c>ImageServiceRaster.LoadAsync</c> reads <c>fullExtent</c> (not <c>extent</c>)
    /// and fails configuration parsing when it is absent (#1456).
    /// </summary>
    [JsonPropertyName("fullExtent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageServerExtent? FullExtent { get; init; }

    /// <summary>
    /// Initial display extent of the image service. Esri clients read this when
    /// first loading the layer; mirror <see cref="FullExtent"/> when unspecified (#1456).
    /// </summary>
    [JsonPropertyName("initialExtent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageServerExtent? InitialExtent { get; init; }

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

    // Esri ImageServers always emit a `fields` array (the raster catalog
    // attribute fields). Default to an empty array rather than null: the ArcGIS
    // Maps SDK for JavaScript calls fields.find(...) during ImageryLayer.load()
    // and throws "Cannot read properties of null (reading 'find')" on a null.
    [JsonPropertyName("fields")]
    public Field[] Fields { get; init; } = [];

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
    public int MaxImageHeight { get; init; } = 4096;

    [JsonPropertyName("maxImageWidth")]
    public int MaxImageWidth { get; init; } = 4096;

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

    /// <summary>
    /// Pixel-block storage characteristics. The native .NET ArcGIS Runtime
    /// <c>ImageServiceRaster.LoadAsync</c> requires <c>storageInfo</c> (with
    /// <c>blockWidth</c>/<c>blockHeight</c>) to read configuration data; without it
    /// the load fails with "Failed to read configuration data" (#1456).
    /// </summary>
    [JsonPropertyName("storageInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageServerStorageInfo? StorageInfo { get; init; }

    /// <summary>
    /// Pixel-block width at the ImageServer metadata root. Esri's ImageServer root
    /// surfaces <c>blockWidth</c> both at the top level AND inside <c>storageInfo</c>.
    /// The ArcGIS Maps SDK for .NET <c>ImageServiceRaster</c> reads it from the root,
    /// so it must be emitted there too, not only nested under <c>storageInfo</c> (#1456).
    /// </summary>
    [JsonPropertyName("blockWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BlockWidth { get; init; }

    /// <summary>
    /// Pixel-block height at the ImageServer metadata root. Mirrors the value inside
    /// <c>storageInfo</c> for the ArcGIS Maps SDK for .NET <c>ImageServiceRaster</c>,
    /// which reads it from the root (#1456).
    /// </summary>
    [JsonPropertyName("blockHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BlockHeight { get; init; }

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

    /// <summary>
    /// Temporal metadata describing the layer's time-aware fields and extent.
    /// Emitted only when the catalog metadata declares time information.
    /// </summary>
    [JsonPropertyName("timeInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageServerTimeInfo? TimeInfo { get; init; }

    /// <summary>
    /// Indicates that the layer represents a multidimensional raster (cube).
    /// Always emitted; defaults to <c>false</c> until cube ingestion ships.
    /// </summary>
    [JsonPropertyName("hasMultidimensions")]
    public bool HasMultidimensions { get; init; } = false;

    /// <summary>
    /// Multidimensional metadata when the layer is a cube.
    /// Emitted only when populated; remains null until cube ingestion lands.
    /// </summary>
    [JsonPropertyName("multidimensionalInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageServerMultidimensionalInfo? MultidimensionalInfo { get; init; }
}

/// <summary>
/// Esri-compatible time info for an Image Server layer.
/// </summary>
public sealed class ImageServerTimeInfo
{
    /// <summary>
    /// Field carrying the start time for raster catalog items, when present.
    /// </summary>
    [JsonPropertyName("startTimeField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartTimeField { get; init; }

    /// <summary>
    /// Field carrying the end time for raster catalog items, when present.
    /// </summary>
    [JsonPropertyName("endTimeField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Optional track identifier field used for temporal visualisation.
    /// </summary>
    [JsonPropertyName("trackIdField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrackIdField { get; init; }

    /// <summary>
    /// Inclusive [start, end] temporal extent in milliseconds since epoch when known.
    /// Null entries are emitted when only one bound is known; the array itself is
    /// omitted entirely when no extent is known so probing clients see a stable shape.
    /// </summary>
    [JsonPropertyName("timeExtent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long?[]? TimeExtent { get; init; }

    /// <summary>
    /// Time reference (zone + DST handling) used by Esri clients.
    /// </summary>
    [JsonPropertyName("timeReference")]
    public ImageServerTimeReference TimeReference { get; init; } = new();

    /// <summary>
    /// Default time interval used when the client does not specify one.
    /// </summary>
    [JsonPropertyName("defaultTimeInterval")]
    public int DefaultTimeInterval { get; init; }

    /// <summary>
    /// Units for <see cref="DefaultTimeInterval"/>.
    /// </summary>
    [JsonPropertyName("defaultTimeIntervalUnits")]
    public string DefaultTimeIntervalUnits { get; init; } = "esriTimeUnitsUnknown";

    /// <summary>
    /// Indicates whether the layer streams live observations.
    /// </summary>
    [JsonPropertyName("hasLiveData")]
    public bool HasLiveData { get; init; }
}

/// <summary>
/// Esri-compatible time reference describing the timezone semantics of a temporal layer.
/// </summary>
public sealed class ImageServerTimeReference
{
    /// <summary>
    /// IANA-compatible timezone identifier. Defaults to UTC.
    /// </summary>
    [JsonPropertyName("timeZone")]
    public string TimeZone { get; init; } = "UTC";

    /// <summary>
    /// Whether the layer's timestamps observe daylight saving transitions.
    /// </summary>
    [JsonPropertyName("respectsDaylightSaving")]
    public bool RespectsDaylightSaving { get; init; }
}

/// <summary>
/// Esri-compatible <c>multidimensionalInfo</c> document describing the variables
/// and dimensional axes of a multidimensional raster (cube). Mirrors the shape
/// the ArcGIS Maps SDK reads via <c>ImageryLayer.multidimensional_info</c>.
/// </summary>
public sealed class ImageServerMultidimensionalInfo
{
    /// <summary>
    /// Variables (data fields) declared by the cube, each carrying its own
    /// dimensional axes. Empty when the layer is not multidimensional.
    /// </summary>
    [JsonPropertyName("variables")]
    public ImageServerMultidimensionalVariable[] Variables { get; init; } = [];
}

/// <summary>
/// Single variable inside an Image Server multidimensional info document.
/// </summary>
public sealed class ImageServerMultidimensionalVariable
{
    /// <summary>
    /// Variable name (e.g. "temperature").
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of the variable.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// Unit of measurement for the variable's values.
    /// </summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>
    /// Dimensional axes the variable is sampled over (e.g. StdTime, StdZ).
    /// </summary>
    [JsonPropertyName("dimensions")]
    public ImageServerMultidimensionalDimension[] Dimensions { get; init; } = [];
}

/// <summary>
/// Single dimensional axis (e.g. <c>StdTime</c>) of a multidimensional variable,
/// following the Esri <c>multidimensionalInfo</c> dimension contract.
/// </summary>
public sealed class ImageServerMultidimensionalDimension
{
    /// <summary>
    /// Dimension name (e.g. "StdTime", "StdZ").
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Unit of the dimension's coordinate values (e.g. "ISO8601", "meters").
    /// </summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>
    /// Inclusive [min, max] extent of the dimension's coordinate values.
    /// Time dimensions are expressed in milliseconds since the Unix epoch.
    /// </summary>
    [JsonPropertyName("extent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Extent { get; init; }

    /// <summary>
    /// Explicit coordinate values along the dimension, when enumerable.
    /// Omitted for large or irregular axes where only the extent is known.
    /// </summary>
    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Values { get; init; }

    /// <summary>
    /// Whether the dimension uses a regular (evenly spaced) interval.
    /// </summary>
    [JsonPropertyName("hasRegularIntervals")]
    public bool HasRegularIntervals { get; init; }

    /// <summary>
    /// Number of discrete slices along the dimension.
    /// </summary>
    [JsonPropertyName("dimensionSize")]
    public long DimensionSize { get; init; }
}

/// <summary>
/// Top-level response for the ImageServer <c>multidimensionalInfo</c> operation.
/// Wraps the <see cref="ImageServerMultidimensionalInfo"/> document under the
/// <c>multidimensionalInfo</c> key, matching the ArcGIS REST contract.
/// </summary>
public sealed class MultidimensionalInfoResponse
{
    /// <summary>
    /// The multidimensional info document. Always present; carries an empty
    /// <c>variables</c> array when the layer is not multidimensional.
    /// </summary>
    [JsonPropertyName("multidimensionalInfo")]
    public required ImageServerMultidimensionalInfo MultidimensionalInfo { get; init; }
}

/// <summary>
/// Single dimension constraint within a slice's multidimensional definition,
/// following the Esri <c>slices</c> contract. Pins a variable's dimension to one or
/// more coordinate values that identify the slice.
/// </summary>
public sealed class ImageServerSliceDimension
{
    /// <summary>
    /// Variable the slice belongs to (e.g. "temperature").
    /// </summary>
    [JsonPropertyName("variableName")]
    public required string VariableName { get; init; }

    /// <summary>
    /// Dimension being pinned (e.g. "StdTime", "StdZ").
    /// </summary>
    [JsonPropertyName("dimensionName")]
    public required string DimensionName { get; init; }

    /// <summary>
    /// Coordinate values that identify this slice along the dimension. Time
    /// dimensions are expressed in milliseconds since the Unix epoch.
    /// </summary>
    [JsonPropertyName("values")]
    public double[] Values { get; init; } = [];
}

/// <summary>
/// Single slice of a multidimensional raster: a unique combination of variable and
/// dimension coordinate values, following the Esri ImageServer <c>slices</c> contract.
/// </summary>
public sealed class ImageServerSlice
{
    /// <summary>
    /// Stable, zero-based identifier of the slice within the response.
    /// </summary>
    [JsonPropertyName("sliceId")]
    public long SliceId { get; init; }

    /// <summary>
    /// The dimension constraints that uniquely identify this slice.
    /// </summary>
    [JsonPropertyName("multidimensionalDefinition")]
    public ImageServerSliceDimension[] MultidimensionalDefinition { get; init; } = [];
}

/// <summary>
/// Top-level response for the ImageServer <c>slices</c> operation. Carries the
/// enumerated multidimensional slices under the <c>slices</c> key, matching the
/// ArcGIS REST contract. The array is empty when the layer is not multidimensional
/// or its dimension coordinate values are not enumerable.
/// </summary>
public sealed class SlicesResponse
{
    /// <summary>
    /// The enumerated multidimensional slices. Always present; empty when there are
    /// no enumerable slices for the layer.
    /// </summary>
    [JsonPropertyName("slices")]
    public ImageServerSlice[] Slices { get; init; } = [];
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
/// Pixel-block storage information for an image service. Describes the tile/block
/// dimensions the service uses when streaming pixels. Esri clients (including the
/// native .NET <c>ImageServiceRaster</c>) read these to plan chunked pixel reads.
/// </summary>
public sealed class ImageServerStorageInfo
{
    /// <summary>
    /// Width, in pixels, of a stored pixel block.
    /// </summary>
    [JsonPropertyName("blockWidth")]
    public required int BlockWidth { get; init; }

    /// <summary>
    /// Height, in pixels, of a stored pixel block.
    /// </summary>
    [JsonPropertyName("blockHeight")]
    public required int BlockHeight { get; init; }

    /// <summary>
    /// Resampling type used when building pyramids.
    /// </summary>
    [JsonPropertyName("pyramidResamplingType")]
    public string PyramidResamplingType { get; init; } = "Bilinear";

    /// <summary>
    /// Pyramid scaling factor between successive resolution levels.
    /// </summary>
    [JsonPropertyName("pyramidScalingFactor")]
    public int PyramidScalingFactor { get; init; } = 2;
}

/// <summary>
/// Storage/configuration descriptor served at the <c>/ImageServer/conf.json</c>
/// resource. The ArcGIS Maps SDK for .NET native runtime probes <c>conf.json</c>
/// when loading an <c>ImageServiceRaster</c>; for a dynamic (non-cached) image
/// service it must receive a well-formed descriptor (200) rather than a 404, which
/// the native runtime reports as "could not read the ImageServer conf". This block
/// explicitly advertises no fused tile cache (<c>singleFusedMapCache=false</c>,
/// <c>tileInfo=null</c>) and surfaces the pixel-block <c>storageInfo</c>, extent, and
/// spatial reference the runtime uses to plan chunked pixel reads.
/// </summary>
public sealed class ImageServerConfInfo
{
    /// <summary>Whether the service is backed by a single fused tile cache.</summary>
    [JsonPropertyName("singleFusedMapCache")]
    public bool SingleFusedMapCache { get; init; }

    /// <summary>Tile cache scheme; null for a dynamic (non-cached) image service.</summary>
    [JsonPropertyName("tileInfo")]
    public TileInfo? TileInfo { get; init; }

    /// <summary>Pixel-block storage information describing chunked pixel reads.</summary>
    [JsonPropertyName("storageInfo")]
    public required ImageServerStorageInfo StorageInfo { get; init; }

    /// <summary>Width, in pixels, of a stored pixel block (mirrors storageInfo).</summary>
    [JsonPropertyName("blockWidth")]
    public required int BlockWidth { get; init; }

    /// <summary>Height, in pixels, of a stored pixel block (mirrors storageInfo).</summary>
    [JsonPropertyName("blockHeight")]
    public required int BlockHeight { get; init; }

    /// <summary>Spatial reference of the image service.</summary>
    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }

    /// <summary>Full extent of the image service.</summary>
    [JsonPropertyName("fullExtent")]
    public required ImageServerExtent FullExtent { get; init; }

    /// <summary>Esri pixel type advertised by the service (e.g. U8, F32).</summary>
    [JsonPropertyName("pixelType")]
    public required string PixelType { get; init; }

    /// <summary>Number of bands in the source raster mosaic.</summary>
    [JsonPropertyName("bandCount")]
    public required int BandCount { get; init; }
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

    // Public /exportImage follows the ArcGIS contract: explicit width,height only.
    [RegularExpression(@"^\d{1,4},\d{1,4}$",
        ErrorMessage = "Size must be a comma-separated width,height pair")]
    [StringLength(11, ErrorMessage = "Size is too long")]
    public string? Size { get; init; }

    // No RegularExpression constraint: the handler parses imageSR/bboxSR through
    // SpatialReferenceHelpers, which accepts bare SRIDs, EPSG:/URN/OGC URI forms,
    // CRS84, and the Esri JSON spatial-reference form ({"wkid":N}/{"latestWkid":N})
    // that ArcGIS SDK clients (ArcGIS API for Python export_image) send. A digits-only
    // regex would advertise an incorrect contract and reject those valid SDK forms.
    [StringLength(512, ErrorMessage = "ImageSr is too long")]
    public string? ImageSr { get; init; }

    [StringLength(512, ErrorMessage = "BboxSr is too long")]
    public string? BboxSr { get; init; }

    // Accepts the Esri ImageServer format tokens the ArcGIS SDKs send. png8/png24/png32
    // and jpgpng are normalised to a concrete encoding by the handler; bmp and gif are
    // accepted for shape but rejected with a clear 400 because the shared raster export
    // pipeline only emits png/jpeg/tiff containers.
    [RegularExpression(@"(?i)^(png|png8|png24|png32|jpgpng|jpg|jpeg|tiff|tif|bmp|gif)$",
        ErrorMessage = "Format must be one of png, png8, png24, png32, jpgpng, jpg, jpeg, tiff, tif, bmp, or gif")]
    public string? Format { get; init; } = "png";

    [RegularExpression(@"(?i)^(C128|C64|F32|F64|S16|S32|S8|U1|U16|U2|U32|U4|U8|UNKNOWN)$",
        ErrorMessage = "PixelType must be one of the ArcGIS ImageServer pixel type values")]
    public string? PixelType { get; init; }

    [StringLength(100, ErrorMessage = "NoData value is too long")]
    public string? NoData { get; init; }

    [StringLength(50, ErrorMessage = "NoDataInterpretation is too long")]
    public string? NoDataInterpretation { get; init; } = "esriNoDataMatchAny";

    [StringLength(50, ErrorMessage = "Interpolation value is too long")]
    public string? Interpolation { get; init; } = "RSP_BilinearInterpolation";

    [RegularExpression(@"(?i)^(none|jpeg|lz77)$",
        ErrorMessage = "Compression must be one of: None, JPEG, or LZ77")]
    public string? Compression { get; init; }

    [Range(0, 100, ErrorMessage = "CompressionQuality must be between 0 and 100")]
    public int? CompressionQuality { get; init; } = 75;

    [StringLength(100, ErrorMessage = "BandIds is too long")]
    public string? BandIds { get; init; }

    [StringLength(1000, ErrorMessage = "MosaicRule is too long")]
    public string? MosaicRule { get; init; }

    [StringLength(1000, ErrorMessage = "RenderingRule is too long")]
    public string? RenderingRule { get; init; }

    [StringLength(100, ErrorMessage = "Time is too long")]
    public string? Time { get; init; }

    [RegularExpression(@"^(json|pjson|image)$", ErrorMessage = "Format parameter must be 'json', 'pjson', or 'image'")]
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
