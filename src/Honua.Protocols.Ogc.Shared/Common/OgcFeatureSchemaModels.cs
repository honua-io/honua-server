// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// OGC API Features Part 5 (Schemas) document for a collection: the JSON Schema of a
/// feature's properties, including the properties that are not filterable.
/// </summary>
/// <remarks>
/// This is distinct from <see cref="QueryablesSchema"/> on purpose. Queryables list the
/// properties a filter may reference and carry a typed geometry property; the feature
/// schema lists every property a feature carries and marks the ones with an OGC role
/// (<c>id</c>, <c>primary-geometry</c>) through <c>x-ogc-role</c>. Clients read the two
/// for different reasons: QGIS, for one, builds a layer's fields from this document when
/// the collection advertises it, and falls back to sampling items otherwise - which
/// leaves an empty collection with no fields at all.
/// </remarks>
public sealed record FeatureSchemaDocument
{
    /// <summary>Stable identifier for the schema resource.</summary>
    [JsonPropertyName("$id")]
    public string? Id { get; init; }

    /// <summary>JSON Schema specification version.</summary>
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>Schema document type (always "object").</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    /// <summary>Human-readable title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Optional description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Schema definitions for the feature's properties.</summary>
    [JsonPropertyName("properties")]
    public required ImmutableDictionary<string, FeatureSchemaProperty> Properties { get; init; }

    /// <summary>Names of the properties a feature must carry (non-nullable, not server-assigned).</summary>
    [JsonPropertyName("required")]
    public ImmutableArray<string>? Required { get; init; }
}

/// <summary>
/// One property of a <see cref="FeatureSchemaDocument"/>.
/// </summary>
public sealed record FeatureSchemaProperty
{
    /// <summary>
    /// JSON Schema type. Null for the primary geometry, which Part 5 describes through
    /// <see cref="Format"/> alone: a geometry is not a JSON object the client should
    /// treat as an attribute.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Human-readable title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Detailed description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Format hint: <c>date-time</c>, <c>date</c>, <c>uuid</c>, or one of the Part 5
    /// <c>geometry-*</c> values for the primary geometry.
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Maximum length for string properties.</summary>
    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; init; }

    /// <summary>Enumerated values for coded domains.</summary>
    [JsonPropertyName("enum")]
    public ImmutableArray<object>? Enum { get; init; }

    /// <summary>
    /// OGC role of the property: <c>id</c> for the feature identifier,
    /// <c>primary-geometry</c> for the geometry.
    /// </summary>
    [JsonPropertyName("x-ogc-role")]
    public string? OgcRole { get; init; }

    /// <summary>True when the server assigns the value and a client must not send it.</summary>
    [JsonPropertyName("readOnly")]
    public bool? ReadOnly { get; init; }

    /// <summary>Property order, for clients that lay fields out in schema order.</summary>
    [JsonPropertyName("x-ogc-propertySeq")]
    public int? PropertySeq { get; init; }
}
