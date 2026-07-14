// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.MapServer.Models;

/// <summary>
/// ArcGIS-compatible submission response for an asynchronous MapServer <c>exportTiles</c> job
/// (explicit Compact Cache V2 / TPKX negotiation). Mirrors the Esri job envelope: a durable
/// <c>jobId</c> plus an <c>esriJob*</c> status the client polls.
/// </summary>
internal sealed class ExportTilesJobSubmitResponse
{
    /// <summary>Durable job identifier the client polls for status and results.</summary>
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    /// <summary>Esri job status; <c>esriJobSubmitted</c> on acceptance.</summary>
    [JsonPropertyName("jobStatus")]
    public string JobStatus { get; init; } = "esriJobSubmitted";
}

/// <summary>
/// ArcGIS-compatible status response for an asynchronous MapServer <c>exportTiles</c> job.
/// </summary>
internal sealed class ExportTilesJobStatusResponse
{
    /// <summary>Durable job identifier.</summary>
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    /// <summary>Esri job status mapped from the canonical execution status.</summary>
    [JsonPropertyName("jobStatus")]
    public required string JobStatus { get; init; }

    /// <summary>Optional progress percentage when the runtime reports it.</summary>
    [JsonPropertyName("percentComplete")]
    public double? PercentComplete { get; init; }

    /// <summary>Structured job messages (for example a sanitized failure description).</summary>
    [JsonPropertyName("messages")]
    public IReadOnlyList<ExportTilesJobMessage> Messages { get; init; } = [];
}

/// <summary>A single ArcGIS job message.</summary>
internal sealed class ExportTilesJobMessage
{
    /// <summary>Esri message type (for example <c>esriJobMessageTypeError</c>).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Sanitized, human-readable message description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }
}

/// <summary>
/// ArcGIS-compatible result response for a completed asynchronous MapServer <c>exportTiles</c>
/// job, exposing the freshly signed output package URL under <c>out_service_url</c>.
/// </summary>
internal sealed class ExportTilesJobResultResponse
{
    /// <summary>Durable job identifier.</summary>
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    /// <summary>The <c>out_service_url</c> result parameter carrying the signed package URL.</summary>
    [JsonPropertyName("results")]
    public required ExportTilesJobResults Results { get; init; }
}

/// <summary>The <c>results</c> object for a completed export job.</summary>
internal sealed class ExportTilesJobResults
{
    /// <summary>Freshly signed, time-limited URL to the exported tile package.</summary>
    [JsonPropertyName("out_service_url")]
    public required ExportTilesJobResultValue OutServiceUrl { get; init; }
}

/// <summary>A single ArcGIS job result parameter value.</summary>
internal sealed class ExportTilesJobResultValue
{
    /// <summary>The signed package URL.</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>Expiry of the signed URL / artifact retention horizon.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}
