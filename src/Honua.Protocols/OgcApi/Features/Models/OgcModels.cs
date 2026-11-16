// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features.Models;

public sealed record BatchRequest
{
    [JsonPropertyName("operations")]
    public required List<BatchOperation> Operations { get; init; }

    [JsonPropertyName("failFast")]
    public bool FailFast { get; init; } = false;
}

public sealed record BatchOperation
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("featureId")]
    public string? FeatureId { get; init; }

    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; init; }
}

public sealed record BatchOperationResponse
{
    [JsonPropertyName("results")]
    public required List<BatchOperationResult> Results { get; init; }

    [JsonPropertyName("hasErrors")]
    public bool HasErrors { get; init; }

    [JsonPropertyName("processedCount")]
    public int ProcessedCount { get; init; }

    [JsonPropertyName("successCount")]
    public int SuccessCount { get; init; }
}

public sealed record BatchOperationResult
{
    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    [JsonPropertyName("isSuccess")]
    public bool IsSuccess { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; }

    [JsonPropertyName("featureId")]
    public string? FeatureId { get; init; }
}
