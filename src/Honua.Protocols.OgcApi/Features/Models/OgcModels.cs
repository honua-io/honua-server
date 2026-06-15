// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features.Models;

/// <summary>
/// OGC API Features Part 3 Queryables schema response
/// JSON Schema document describing filterable properties
/// </summary>
public sealed record QueryablesSchema
{
    /// <summary>
    /// Stable identifier for the queryables schema resource.
    /// </summary>
    [JsonPropertyName("$id")]
    public string? Id { get; init; }

    /// <summary>
    /// JSON Schema specification version
    /// </summary>
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// Schema document type (always "object" for queryables)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    /// <summary>
    /// Human-readable title for the queryables schema
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Optional description for the queryables schema
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Schema definitions for queryable properties
    /// </summary>
    [JsonPropertyName("properties")]
    public required ImmutableDictionary<string, JsonSchemaProperty> Properties { get; init; }

    /// <summary>
    /// Array of required property names (non-nullable fields)
    /// </summary>
    [JsonPropertyName("required")]
    public ImmutableArray<string>? Required { get; init; }

    /// <summary>
    /// Additional properties are not allowed by default for queryables
    /// </summary>
    [JsonPropertyName("additionalProperties")]
    public bool AdditionalProperties { get; init; } = false;
}

/// <summary>
/// JSON Schema property definition for a queryable field
/// </summary>
public sealed record JsonSchemaProperty
{
    /// <summary>
    /// JSON Schema type (string, number, integer, boolean, array, object)
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Human-readable title/description of the property
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Detailed description of the property
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Format hint for string types (date-time, date, time, etc.)
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>
    /// Maximum length for string properties
    /// </summary>
    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; init; }

    /// <summary>
    /// Default value for the property
    /// </summary>
    [JsonPropertyName("default")]
    public object? Default { get; init; }

    /// <summary>
    /// Enumerated values for coded domains or boolean-like fields
    /// </summary>
    [JsonPropertyName("enum")]
    public ImmutableArray<object>? Enum { get; init; }

    /// <summary>
    /// For geometry properties - reference to GeoJSON geometry schema
    /// </summary>
    [JsonPropertyName("$ref")]
    public string? Ref { get; init; }
}

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
