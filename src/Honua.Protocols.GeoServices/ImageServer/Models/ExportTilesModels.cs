// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ImageServerExportTilesEstimateResponse
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

public sealed class ImageServerExportTilesResponse
{
    [JsonPropertyName("jobStatus")]
    public string JobStatus { get; init; } = "esriJobSucceeded";

    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

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
    public ImageServerExportTilesFileInfo[] Files { get; init; } = [];

    [JsonPropertyName("results")]
    public ImageServerExportTilesResults? Results { get; init; }
}

public sealed class ImageServerExportTilesFileInfo
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

public sealed class ImageServerExportTilesResults
{
    [JsonPropertyName("out_service_url")]
    public ImageServerExportTilesResultValue? OutServiceUrl { get; init; }
}

public sealed class ImageServerExportTilesResultValue
{
    [JsonPropertyName("paramUrl")]
    public string? ParamUrl { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}
