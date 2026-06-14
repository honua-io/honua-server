// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Size estimate for a storage-backed ImageServer <c>exportTiles</c> archive.
/// </summary>
public sealed class ImageServerExportTilesEstimateResponse
{
    /// <summary>
    /// Number of WebMercatorQuad tiles selected by the request after limits.
    /// </summary>
    [JsonPropertyName("tileCount")]
    public long TileCount { get; init; }

    /// <summary>
    /// Estimated archive size, in bytes.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>
    /// Unit used by <see cref="Size"/>.
    /// </summary>
    [JsonPropertyName("sizeUnit")]
    public string SizeUnit { get; init; } = "bytes";

    /// <summary>
    /// Estimated archive size, in bytes.
    /// </summary>
    [JsonPropertyName("estimatedSizeBytes")]
    public long EstimatedSizeBytes { get; init; }

    /// <summary>
    /// Minimum zoom level included.
    /// </summary>
    [JsonPropertyName("minZoom")]
    public int MinZoom { get; init; }

    /// <summary>
    /// Maximum zoom level included.
    /// </summary>
    [JsonPropertyName("maxZoom")]
    public int MaxZoom { get; init; }

    /// <summary>
    /// Whether the output is an Esri tile package. Honua currently returns a ZIP archive.
    /// </summary>
    [JsonPropertyName("tilePackage")]
    public bool TilePackage { get; init; }

    /// <summary>
    /// Storage archive format produced by the current implementation.
    /// </summary>
    [JsonPropertyName("storageFormat")]
    public string StorageFormat { get; init; } = "zip";

    /// <summary>
    /// MIME type of the exported archive.
    /// </summary>
    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "application/zip";

    /// <summary>
    /// Indicates that the request selected more tiles than the configured operation limit.
    /// </summary>
    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; init; }
}

/// <summary>
/// Response for a completed storage-backed ImageServer <c>exportTiles</c> archive.
/// </summary>
public sealed class ImageServerExportTilesResponse
{
    /// <summary>
    /// ArcGIS-compatible job status string for the completed synchronous export.
    /// </summary>
    [JsonPropertyName("jobStatus")]
    public string JobStatus { get; init; } = "esriJobSucceeded";

    /// <summary>
    /// ImageServer layer identifier used for the export.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// Number of tiles included in the archive.
    /// </summary>
    [JsonPropertyName("tileCount")]
    public long TileCount { get; init; }

    /// <summary>
    /// Minimum zoom level included.
    /// </summary>
    [JsonPropertyName("minZoom")]
    public int MinZoom { get; init; }

    /// <summary>
    /// Maximum zoom level included.
    /// </summary>
    [JsonPropertyName("maxZoom")]
    public int MaxZoom { get; init; }

    /// <summary>
    /// Whether the output is an Esri tile package. Honua currently returns a ZIP archive.
    /// </summary>
    [JsonPropertyName("tilePackage")]
    public bool TilePackage { get; init; }

    /// <summary>
    /// Storage archive format produced by the current implementation.
    /// </summary>
    [JsonPropertyName("storageFormat")]
    public string StorageFormat { get; init; } = "zip";

    /// <summary>
    /// MIME type of the exported archive.
    /// </summary>
    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "application/zip";

    /// <summary>
    /// Cloud file identifier for the archive.
    /// </summary>
    [JsonPropertyName("archiveFileId")]
    public string? ArchiveFileId { get; init; }

    /// <summary>
    /// Cloud file identifier for the archive.
    /// </summary>
    [JsonPropertyName("fileId")]
    public string? FileId { get; init; }

    /// <summary>
    /// Archive size in bytes.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>
    /// Archive size in bytes.
    /// </summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    /// <summary>
    /// Presigned or provider-specific download URL when the storage provider can create one.
    /// </summary>
    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// Time when the temporary archive expires.
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// WGS84 bounds used for the export: west, south, east, north.
    /// </summary>
    [JsonPropertyName("bounds")]
    public double[]? Bounds { get; init; }

    /// <summary>
    /// Exported archive files.
    /// </summary>
    [JsonPropertyName("files")]
    public ImageServerExportTilesFileInfo[] Files { get; init; } = [];

    /// <summary>
    /// ArcGIS-style results container for clients looking for output parameters.
    /// </summary>
    [JsonPropertyName("results")]
    public ImageServerExportTilesResults? Results { get; init; }
}

/// <summary>
/// File entry emitted by the ImageServer <c>exportTiles</c> response.
/// </summary>
public sealed class ImageServerExportTilesFileInfo
{
    /// <summary>
    /// Archive filename.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Cloud file identifier.
    /// </summary>
    [JsonPropertyName("fileId")]
    public string? FileId { get; init; }

    /// <summary>
    /// Download URL for the archive.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>
    /// MIME type of the archive.
    /// </summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }
}

/// <summary>
/// ArcGIS-style output parameter map for ImageServer <c>exportTiles</c>.
/// </summary>
public sealed class ImageServerExportTilesResults
{
    /// <summary>
    /// Output service/archive URL parameter.
    /// </summary>
    [JsonPropertyName("out_service_url")]
    public ImageServerExportTilesResultValue? OutServiceUrl { get; init; }
}

/// <summary>
/// ArcGIS-style ImageServer <c>exportTiles</c> result parameter value.
/// </summary>
public sealed class ImageServerExportTilesResultValue
{
    /// <summary>
    /// URL to the output parameter resource.
    /// </summary>
    [JsonPropertyName("paramUrl")]
    public string? ParamUrl { get; init; }

    /// <summary>
    /// Direct value for clients that do not follow parameter URLs.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}
