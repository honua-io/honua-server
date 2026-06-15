// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Common;

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

    private static string? ReadStringId(ref Utf8JsonReader reader)
    {
        var str = reader.GetString();
        if (str == null)
        {
            return null;
        }

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
