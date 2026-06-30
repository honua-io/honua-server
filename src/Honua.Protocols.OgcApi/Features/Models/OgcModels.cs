// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features.Models;

/// <summary>
/// Request for batch feature operations
/// </summary>
public sealed record BatchRequest
{
    /// <summary>
    /// List of operations to perform in the batch
    /// </summary>
    [JsonPropertyName("operations")]
    public required List<BatchOperation> Operations { get; init; }

    /// <summary>
    /// Whether to stop processing on first error
    /// </summary>
    [JsonPropertyName("failFast")]
    public bool FailFast { get; init; } = false;
}

/// <summary>
/// Single operation in a batch request
/// </summary>
public sealed record BatchOperation
{
    /// <summary>
    /// Unique identifier for this operation
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>
    /// Type of operation (CREATE, UPDATE, DELETE)
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Feature ID for UPDATE or DELETE operations
    /// </summary>
    [JsonPropertyName("featureId")]
    public string? FeatureId { get; init; }

    /// <summary>
    /// Feature data for CREATE or UPDATE operations
    /// </summary>
    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; init; }
}

/// <summary>
/// Response from batch operation
/// </summary>
public sealed record BatchOperationResponse
{
    /// <summary>
    /// Results for each operation
    /// </summary>
    [JsonPropertyName("results")]
    public required List<BatchOperationResult> Results { get; init; }

    /// <summary>
    /// Whether any operations failed
    /// </summary>
    [JsonPropertyName("hasErrors")]
    public bool HasErrors { get; init; }

    /// <summary>
    /// Number of operations processed
    /// </summary>
    [JsonPropertyName("processedCount")]
    public int ProcessedCount { get; init; }

    /// <summary>
    /// Number of successful operations
    /// </summary>
    [JsonPropertyName("successCount")]
    public int SuccessCount { get; init; }
}

/// <summary>
/// Result of a single operation in a batch
/// </summary>
public sealed record BatchOperationResult
{
    /// <summary>
    /// Operation identifier
    /// </summary>
    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    /// <summary>
    /// Whether the operation succeeded
    /// </summary>
    [JsonPropertyName("isSuccess")]
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Error message if operation failed
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// HTTP status code for the operation
    /// </summary>
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; }

    /// <summary>
    /// Feature ID if operation created or updated a feature
    /// </summary>
    [JsonPropertyName("featureId")]
    public string? FeatureId { get; init; }
}
