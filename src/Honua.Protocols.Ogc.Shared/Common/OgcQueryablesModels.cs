// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// OGC API Features Part 3 Queryables schema response
/// JSON Schema document describing filterable properties.
/// Shared by the OGC API Features adapter and the STAC Filter Extension
/// (both emit a JSON Schema queryables document), so it lives in the neutral
/// OGC shared foundation rather than either protocol adapter.
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
