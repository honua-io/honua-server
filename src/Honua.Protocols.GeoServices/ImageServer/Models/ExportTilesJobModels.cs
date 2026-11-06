// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ImageServerExportTilesJobSubmitResponse
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("jobStatus")]
    public string JobStatus { get; init; } = "esriJobSubmitted";
}

public sealed class ImageServerExportTilesJobStatusResponse
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("jobStatus")]
    public required string JobStatus { get; init; }

    [JsonPropertyName("percentComplete")]
    public double? PercentComplete { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<ImageServerExportTilesJobMessage> Messages { get; init; } = [];
}

public sealed class ImageServerExportTilesJobMessage
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }
}

public sealed class ImageServerExportTilesJobResultResponse
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("results")]
    public required ImageServerExportTilesJobResults Results { get; init; }
}

public sealed class ImageServerExportTilesJobResults
{
    [JsonPropertyName("out_service_url")]
    public required ImageServerExportTilesJobResultValue OutServiceUrl { get; init; }
}

public sealed class ImageServerExportTilesJobResultValue
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}
