// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Ogc.Common;

namespace Honua.Server.Features.OgcFeatures.Models;

/// <summary>
/// JSON converter for raw JSON strings to avoid double encoding
/// </summary>
public sealed class RawJsonStringConverter : JsonConverter<string?>
{
    /// <summary>
    /// Reads and converts JSON to a raw JSON string value.
    /// </summary>
    /// <param name="reader">The reader to read from.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The raw JSON string, or null if the token is null.</returns>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    /// <summary>
    /// Writes a raw JSON string value to the writer.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The raw JSON string value to write.</param>
    /// <param name="options">The serializer options.</param>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            // Write the string as raw JSON without quotes
            writer.WriteRawValue(value);
        }
    }
}

/// <summary>
/// Simple GeoJSON geometry representation for AOT compatibility
/// </summary>
public sealed record SimpleGeoJsonGeometry
{
    /// <summary>
    /// Geometry type (Point, LineString, Polygon, etc.)
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Geometry coordinates as raw JSON string for AOT compatibility
    /// </summary>
    [JsonPropertyName("coordinates")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? CoordinatesJson { get; init; }

    /// <summary>
    /// Geometry collection members as raw JSON string for AOT compatibility
    /// </summary>
    [JsonPropertyName("geometries")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? GeometriesJson { get; init; }
}

/// <summary>
/// JSON converter for GeoJSON feature IDs that supports both string and number values
/// per RFC 7946 Section 3.2.
/// </summary>
public sealed class FeatureIdConverter : JsonConverter<object?>
{
    /// <inheritdoc />
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out var longValue) => longValue,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String => ReadStringId(ref reader),
            JsonTokenType.Null => null,
            _ => null
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static object? ReadStringId(ref Utf8JsonReader reader)
    {
        var str = reader.GetString();
        if (str == null)
        {
            return null;
        }

        // Try to parse as long for numeric string IDs
        if (long.TryParse(str, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        // Keep as string for non-numeric IDs (UUID, slug, etc.)
        return str;
    }
}

/// <summary>
/// GeoJSON Feature for items response
/// </summary>
public sealed record GeoJsonFeature
{
    /// <summary>
    /// GeoJSON object type (always "Feature")
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    /// <summary>
    /// Feature identifier. Per RFC 7946, this can be a number or string.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FeatureIdConverter))]
    public object? Id { get; init; }

    /// <summary>
    /// Feature geometry in GeoJSON format
    /// </summary>
    [JsonPropertyName("geometry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public SimpleGeoJsonGeometry? Geometry { get; init; }

    /// <summary>
    /// Feature properties (attributes)
    /// </summary>
    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; init; } = new();

    /// <summary>
    /// Links to related resources (self, collection, etc.)
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }
}

/// <summary>
/// GeoJSON FeatureCollection for OGC API Features Items response
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "FeatureCollection is the standard GeoJSON type name")]
public sealed record FeatureCollection : ICollectionResponse<GeoJsonFeature>
{
    /// <summary>
    /// GeoJSON object type (always "FeatureCollection")
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    /// <summary>
    /// Array of GeoJSON Feature objects
    /// </summary>
    [JsonPropertyName("features")]
    public required GeoJsonFeature[] Features { get; init; }

    /// <summary>
    /// Number of features matched by the query (before pagination)
    /// </summary>
    [JsonPropertyName("numberMatched")]
    public long? NumberMatched { get; init; }

    /// <summary>
    /// Number of features returned in this response (after pagination)
    /// </summary>
    [JsonPropertyName("numberReturned")]
    public int NumberReturned { get; init; }

    /// <summary>
    /// Links to related resources (pagination, etc.)
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }

    /// <summary>
    /// Timestamp when the collection was generated
    /// </summary>
    [JsonPropertyName("timeStamp")]
    public DateTimeOffset? TimeStamp { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<GeoJsonFeature> ICollectionResponse<GeoJsonFeature>.Items => Features.ToImmutableArray();
    ImmutableArray<ILink>? ICollectionResponse<GeoJsonFeature>.Links => Links?.Cast<ILink>().ToImmutableArray();
    IPaginationMetadata? ICollectionResponse<GeoJsonFeature>.Pagination =>
        NumberMatched.HasValue || NumberReturned > 0
            ? PaginationMetadata.Create(NumberMatched, NumberReturned, false)
            : null;
}

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
