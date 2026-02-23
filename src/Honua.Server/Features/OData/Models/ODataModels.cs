// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.OData.Models;

/// <summary>
/// OData service document response
/// </summary>
public sealed class ServiceDocument
{
    /// <summary>
    /// OData context URL
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    /// <summary>
    /// Available entity sets
    /// </summary>
    [JsonPropertyName("value")]
    public required EntitySet[] Value { get; init; }
}

/// <summary>
/// OData entity set definition
/// </summary>
public sealed class EntitySet
{
    /// <summary>
    /// Entity set name
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Entity set kind discriminator.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "EntitySet";

    /// <summary>
    /// Entity set URL
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

/// <summary>
/// OData-specific link implementation for ILink compatibility
/// </summary>
public sealed record ODataLink : ILink
{
    /// <summary>
    /// The URI of the linked resource
    /// </summary>
    public required string Href { get; init; }

    /// <summary>
    /// Relation type (always "next" for OData pagination)
    /// </summary>
    public string? Rel { get; init; }

    /// <summary>
    /// MIME type (always JSON for OData)
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Human-readable title for the link
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Creates an OData pagination link
    /// </summary>
    /// <param name="href">URL to the next page</param>
    /// <returns>New OData link instance</returns>
    public static ODataLink CreateNextLink(string href)
        => new()
        {
            Href = href,
            Rel = "next",
            Type = "application/json",
            Title = "Next Page"
        };
}

/// <summary>
/// Generic OData collection response
/// </summary>
public sealed class ODataResponse : ICollectionResponse<object>
{
    /// <summary>
    /// OData context URL
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    /// <summary>
    /// Total count of items (when $count=true)
    /// </summary>
    [JsonPropertyName("@odata.count")]
    public long? Count { get; init; }

    /// <summary>
    /// Next link for pagination
    /// </summary>
    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }

    /// <summary>
    /// Delta link for change tracking. When present, clients can use this URL
    /// with $deltatoken to retrieve only features modified since the snapshot.
    /// </summary>
    [JsonPropertyName("@odata.deltaLink")]
    public string? DeltaLink { get; init; }

    /// <summary>
    /// Collection of data items
    /// </summary>
    [JsonPropertyName("value")]
    public required object[] Value { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<object> ICollectionResponse<object>.Items => Value.ToImmutableArray();
    ImmutableArray<ILink>? ICollectionResponse<object>.Links =>
        NextLink != null
            ? ImmutableArray.Create<ILink>(ODataLink.CreateNextLink(NextLink))
            : null;
    IPaginationMetadata? ICollectionResponse<object>.Pagination =>
        Count.HasValue || Value.Length > 0
            ? PaginationMetadata.Create(Count, Value.Length, NextLink != null)
            : null;
}

/// <summary>
/// OData error response
/// </summary>
public sealed class ODataError
{
    /// <summary>
    /// Error details
    /// </summary>
    [JsonPropertyName("error")]
    public required ErrorDetails Error { get; init; }
}

/// <summary>
/// OData error details
/// </summary>
public sealed class ErrorDetails
{
    /// <summary>
    /// Error code
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Error message
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Additional error details
    /// </summary>
    [JsonPropertyName("details")]
    public ErrorDetail[]? Details { get; init; }
}

/// <summary>
/// Individual error detail
/// </summary>
public sealed class ErrorDetail
{
    /// <summary>
    /// Error code
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Error message
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Error target
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }
}

/// <summary>
/// OData feature request for create/update operations
/// </summary>
public sealed class ODataFeatureRequest
{
    /// <summary>
    /// Feature geometry in OData spatial (GeoJSON) format
    /// </summary>
    [JsonPropertyName("Geometry")]
    public ODataSpatialGeometry? Geometry { get; set; }

    /// <summary>
    /// Backward-compatible attributes payload
    /// </summary>
    [JsonPropertyName("Attributes")]
    public Dictionary<string, object?>? Attributes { get; set; }

    /// <summary>
    /// Additional properties mapped as dynamic attributes for open types.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// OData single feature response
/// </summary>
public sealed class ODataFeatureResponse
{
    /// <summary>
    /// OData context URL
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    /// <summary>
    /// Feature Object ID
    /// </summary>
    [JsonPropertyName("ObjectId")]
    public long ObjectId { get; init; }

    /// <summary>
    /// Layer ID
    /// </summary>
    [JsonPropertyName("LayerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// Feature geometry in OData spatial (GeoJSON) format
    /// </summary>
    [JsonPropertyName("Geometry")]
    public ODataSpatialGeometry? Geometry { get; init; }

    /// <summary>
    /// Feature attributes as key-value pairs
    /// </summary>
    [JsonPropertyName("Attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }
}

/// <summary>
/// OData spatial CRS representation (GeoJSON-style)
/// </summary>
public sealed class ODataSpatialCrs
{
    /// <summary>
    /// CRS type (typically "name")
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// CRS properties
    /// </summary>
    [JsonPropertyName("properties")]
    public ODataSpatialCrsProperties? Properties { get; init; }
}

/// <summary>
/// OData spatial CRS properties
/// </summary>
public sealed class ODataSpatialCrsProperties
{
    /// <summary>
    /// CRS name (e.g., "EPSG:4326")
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// OData spatial geometry representation
/// </summary>
public sealed class ODataSpatialGeometry
{
    /// <summary>
    /// Geometry type (Point, LineString, Polygon, etc.)
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Geometry coordinates as raw JSON string
    /// </summary>
    [JsonPropertyName("coordinates")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? CoordinatesJson { get; init; }

    /// <summary>
    /// Geometry collection members as raw JSON string
    /// </summary>
    [JsonPropertyName("geometries")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? GeometriesJson { get; init; }

    /// <summary>
    /// Optional CRS definition for the geometry
    /// </summary>
    [JsonPropertyName("crs")]
    public ODataSpatialCrs? Crs { get; init; }
}

/// <summary>
/// Writes raw JSON for geometry coordinates/geometries without quoting.
/// </summary>
internal sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.StartObject or JsonTokenType.StartArray => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            _ => throw new JsonException("Unsupported JSON token for raw JSON value.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteRawValue(value);
        }
    }
}
