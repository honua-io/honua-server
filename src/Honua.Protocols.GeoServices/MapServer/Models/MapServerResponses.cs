// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.Models;

namespace Honua.Protocols.GeoServices.MapServer.Models;

internal sealed class MapServerResponse
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

    [JsonPropertyName("serviceDescription")]
    public string? ServiceDescription { get; init; }

    [JsonPropertyName("mapName")]
    public string? MapName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("copyrightText")]
    public string? CopyrightText { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    [JsonPropertyName("links")]
    public GeoServicesGovernanceLink[]? Links { get; init; }

    [JsonPropertyName("spatialReference")]
    public SpatialReferenceJson? SpatialReference { get; init; }

    [JsonPropertyName("layers")]
    public MapServerLayerInfo[]? Layers { get; init; }

    [JsonPropertyName("tables")]
    public MapServerTableInfo[]? Tables { get; init; }

    [JsonPropertyName("supportsDynamicLayers")]
    public bool SupportsDynamicLayers { get; init; }

    [JsonPropertyName("singleFusedMapCache")]
    public bool SingleFusedMapCache { get; init; }

    [JsonPropertyName("exportTilesAllowed")]
    public bool ExportTilesAllowed { get; init; }

    [JsonPropertyName("maxExportTilesCount")]
    public int MaxExportTilesCount { get; init; }

    [JsonPropertyName("supportedImageFormatTypes")]
    public string? SupportedImageFormatTypes { get; init; }

    [JsonPropertyName("capabilities")]
    public string? Capabilities { get; init; }

    [JsonPropertyName("fullExtent")]
    public GeoServicesExtent? FullExtent { get; init; }

    [JsonPropertyName("initialExtent")]
    public GeoServicesExtent? InitialExtent { get; init; }

    [JsonPropertyName("units")]
    public string Units { get; init; } = "esriMeters";

    [JsonPropertyName("maxImageWidth")]
    public int MaxImageWidth { get; init; } = 4096;

    [JsonPropertyName("maxImageHeight")]
    public int MaxImageHeight { get; init; } = 4096;

    [JsonPropertyName("maxRecordCount")]
    public int MaxRecordCount { get; init; } = 1000;

    [JsonPropertyName("supportedQueryFormats")]
    public string? SupportedQueryFormats { get; init; }

    [JsonPropertyName("supportedExtensions")]
    public string SupportedExtensions { get; init; } = string.Empty;

    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    [JsonPropertyName("documentInfo")]
    public MapServerDocumentInfo? DocumentInfo { get; init; }

    [JsonPropertyName("tileInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileInfo? TileInfo { get; init; }

    [JsonPropertyName("timeInfo")]
    public FeatureServerTimeInfo? TimeInfo { get; init; }
}

internal sealed class ExportTilesEstimateResponse
{
    [JsonPropertyName("tileCount")]
    public long TileCount { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sizeUnit")]
    public string SizeUnit { get; init; } = "bytes";

    [JsonPropertyName("estimatedSizeBytes")]
    public long EstimatedSizeBytes { get; init; }

    [JsonPropertyName("minZoom")]
    public int MinZoom { get; init; }

    [JsonPropertyName("maxZoom")]
    public int MaxZoom { get; init; }

    [JsonPropertyName("tilePackage")]
    public bool TilePackage { get; init; }

    [JsonPropertyName("storageFormat")]
    public string StorageFormat { get; init; } = "zip";

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "application/zip";

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; init; }
}

internal sealed class ExportTilesResponse
{
    [JsonPropertyName("jobStatus")]
    public string JobStatus { get; init; } = "esriJobSucceeded";

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("tileCount")]
    public long TileCount { get; init; }

    [JsonPropertyName("minZoom")]
    public int MinZoom { get; init; }

    [JsonPropertyName("maxZoom")]
    public int MaxZoom { get; init; }

    [JsonPropertyName("tilePackage")]
    public bool TilePackage { get; init; }

    [JsonPropertyName("storageFormat")]
    public string StorageFormat { get; init; } = "zip";

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "application/zip";

    [JsonPropertyName("archiveFileId")]
    public string? ArchiveFileId { get; init; }

    [JsonPropertyName("fileId")]
    public string? FileId { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("bounds")]
    public double[]? Bounds { get; init; }

    [JsonPropertyName("files")]
    public ExportTilesFileInfo[] Files { get; init; } = [];

    [JsonPropertyName("results")]
    public ExportTilesResults? Results { get; init; }
}

internal sealed class ExportTilesFileInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("fileId")]
    public string? FileId { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

internal sealed class ExportTilesResults
{
    [JsonPropertyName("out_service_url")]
    public ExportTilesResultValue? OutServiceUrl { get; init; }
}

internal sealed class ExportTilesResultValue
{
    [JsonPropertyName("paramUrl")]
    public string? ParamUrl { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

internal sealed class MapServerDocumentInfo
{
    [JsonPropertyName("Title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("Author")]
    public string Author { get; init; } = "";

    [JsonPropertyName("Comments")]
    public string Comments { get; init; } = "";

    [JsonPropertyName("Subject")]
    public string Subject { get; init; } = "";

    [JsonPropertyName("Credits")]
    public string Credits { get; init; } = "";

    [JsonPropertyName("Category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("Keywords")]
    public string Keywords { get; init; } = "";
}

internal sealed class MapServerLayerInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("defaultVisibility")]
    public bool DefaultVisibility { get; init; }

    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    [JsonPropertyName("parentLayerId")]
    public int ParentLayerId { get; init; } = -1;

    [JsonPropertyName("subLayerIds")]
    public int[]? SubLayerIds { get; init; }
}

internal sealed class MapServerTableInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed class MapServerLayerResponse
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature Layer";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("copyrightText")]
    public string? CopyrightText { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    [JsonPropertyName("links")]
    public GeoServicesGovernanceLink[]? Links { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("extent")]
    public GeoServicesExtent? Extent { get; init; }

    [JsonPropertyName("spatialReference")]
    public SpatialReferenceJson? SpatialReference { get; init; }

    [JsonPropertyName("sourceSpatialReference")]
    public SpatialReferenceJson? SourceSpatialReference { get; init; }

    [JsonPropertyName("displayField")]
    public string? DisplayField { get; init; }

    [JsonPropertyName("objectIdField")]
    public string? ObjectIdField { get; init; }

    [JsonPropertyName("fields")]
    public MapServerFieldInfo[]? Fields { get; init; }

    [JsonPropertyName("capabilities")]
    public string? Capabilities { get; init; }

    [JsonPropertyName("supportsAdvancedQueries")]
    public bool SupportsAdvancedQueries { get; init; }

    [JsonPropertyName("hasAttachments")]
    public bool HasAttachments { get; init; }

    [JsonPropertyName("relationships")]
    public FeatureServer.Models.LayerRelationshipInfo[] Relationships { get; init; } = [];

    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    [JsonPropertyName("defaultVisibility")]
    public bool DefaultVisibility { get; init; } = true;

    [JsonPropertyName("maxRecordCount")]
    public int MaxRecordCount { get; init; } = 1000;

    [JsonPropertyName("drawingInfo")]
    public object? DrawingInfo { get; init; }

    [JsonPropertyName("definitionExpression")]
    public string? DefinitionExpression { get; init; }

    // Supported query formats are a comma-separated string because Esri clients split the value.
    [JsonPropertyName("supportedQueryFormats")]
    public string? SupportedQueryFormats { get; init; }

    [JsonPropertyName("supportsStatistics")]
    public bool SupportsStatistics { get; init; }

    [JsonPropertyName("supportsOrderBy")]
    public bool SupportsOrderBy { get; init; }

    [JsonPropertyName("supportsDistinct")]
    public bool SupportsDistinct { get; init; }

    [JsonPropertyName("supportsPagination")]
    public bool SupportsPagination { get; init; }

    [JsonPropertyName("advancedQueryCapabilities")]
    public AdvancedQueryCapabilities? AdvancedQueryCapabilities { get; init; }

    [JsonPropertyName("timeInfo")]
    public FeatureServerTimeInfo? TimeInfo { get; init; }

    [JsonPropertyName("parentLayerId")]
    public int ParentLayerId { get; init; } = -1;

    [JsonPropertyName("subLayerIds")]
    public int[]? SubLayerIds { get; init; }
}

internal sealed class MapServerFieldInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("length")]
    public int? Length { get; init; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; } = true;

    [JsonPropertyName("editable")]
    public bool Editable { get; init; } = true;

    [JsonPropertyName("defaultValue")]
    public object? DefaultValue { get; init; }
}

internal sealed class IdentifyResponse
{
    [JsonPropertyName("results")]
    public IdentifyResult[]? Results { get; init; }
}

internal sealed class IdentifyResult
{
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("layerName")]
    public string? LayerName { get; init; }

    [JsonPropertyName("displayFieldName")]
    public string? DisplayFieldName { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("geometry")]
    public object? Geometry { get; init; }
}

internal sealed class FeatureResourceResponse
{
    [JsonPropertyName("feature")]
    public System.Text.Json.JsonElement Feature { get; init; }
}

internal sealed class AllLayersAndTablesResponse
{
    [JsonPropertyName("layers")]
    public MapServerLayerResponse[]? Layers { get; init; }

    [JsonPropertyName("tables")]
    public MapServerLayerResponse[]? Tables { get; init; }
}

internal sealed class MapServerQueryDomainsResponse
{
    [JsonPropertyName("domains")]
    public MapServerDomainInfo[]? Domains { get; init; }
}

internal sealed class MapServerDomainInfo
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("fieldName")]
    public string? FieldName { get; init; }

    [JsonPropertyName("fieldType")]
    public string? FieldType { get; init; }

    [JsonPropertyName("codedValues")]
    public MapServerDomainCodedValue[]? CodedValues { get; init; }

    [JsonPropertyName("range")]
    public object[]? Range { get; init; }

    [JsonPropertyName("mergePolicy")]
    public string? MergePolicy { get; init; }

    [JsonPropertyName("splitPolicy")]
    public string? SplitPolicy { get; init; }
}

internal sealed class MapServerDomainCodedValue
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("code")]
    public object? Code { get; init; }
}

internal sealed class LegendResponse
{
    [JsonPropertyName("layers")]
    public LegendLayerInfo[]? Layers { get; init; }
}

internal sealed class LegendLayerInfo
{
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("layerName")]
    public string? LayerName { get; init; }

    [JsonPropertyName("layerType")]
    public string? LayerType { get; init; }

    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    [JsonPropertyName("legend")]
    public LegendEntry[]? Legend { get; init; }
}

internal sealed class LegendEntry
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("values")]
    public string[]? Values { get; init; }

    [JsonPropertyName("imageData")]
    public string? ImageData { get; init; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "image/png";

    [JsonPropertyName("width")]
    public int Width { get; init; } = 20;

    [JsonPropertyName("height")]
    public int Height { get; init; } = 20;
}

internal sealed class ExportImageResponse
{
    [JsonPropertyName("href")]
    public string? Href { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("extent")]
    public GeoServicesExtent? Extent { get; init; }

    [JsonPropertyName("scale")]
    public double? Scale { get; init; }
}

internal sealed class FindResponse
{
    [JsonPropertyName("results")]
    public FindResult[]? Results { get; init; }
}

internal sealed class FindResult
{
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("layerName")]
    public string? LayerName { get; init; }

    [JsonPropertyName("displayFieldName")]
    public string? DisplayFieldName { get; init; }

    [JsonPropertyName("foundFieldName")]
    public string? FoundFieldName { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("geometry")]
    public object? Geometry { get; init; }
}

internal sealed class TileInfo
{
    [JsonPropertyName("rows")]
    public int Rows { get; init; } = 256;

    [JsonPropertyName("cols")]
    public int Cols { get; init; } = 256;

    [JsonPropertyName("dpi")]
    public int Dpi { get; init; } = 96;

    [JsonPropertyName("format")]
    public string Format { get; init; } = "PNG";

    [JsonPropertyName("origin")]
    public TileOrigin? Origin { get; init; }

    [JsonPropertyName("spatialReference")]
    public SpatialReferenceJson? SpatialReference { get; init; }

    [JsonPropertyName("lods")]
    public LevelOfDetail[]? Lods { get; init; }
}

internal sealed class TileOrigin
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }
}

internal sealed class LevelOfDetail
{
    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("resolution")]
    public double Resolution { get; init; }

    [JsonPropertyName("scale")]
    public double Scale { get; init; }
}
