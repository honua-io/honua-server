// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.MapServer.Models;

internal sealed class ExportTilesJobSubmitResponse
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("jobStatus")]
    public string JobStatus { get; init; } = "esriJobSubmitted";
}

internal sealed class ExportTilesJobStatusResponse
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("jobStatus")]
    public required string JobStatus { get; init; }

    [JsonPropertyName("percentComplete")]
    public double? PercentComplete { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<ExportTilesJobMessage> Messages { get; init; } = [];
}

internal sealed class ExportTilesJobMessage
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }
}

internal sealed class ExportTilesJobResultResponse
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("results")]
    public required ExportTilesJobResults Results { get; init; }
}

internal sealed class ExportTilesJobResults
{
    [JsonPropertyName("out_service_url")]
    public required ExportTilesJobResultValue OutServiceUrl { get; init; }
}

internal sealed class ExportTilesJobResultValue
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}
