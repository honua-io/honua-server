// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.OgcFeatures.Models;

/// <summary>
/// OGC API Features landing page response
/// </summary>
public sealed record LandingPage
{
    /// <summary>
    /// Title of the API
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Description of the API
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Links to related resources
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Conformance declaration response
/// </summary>
public sealed record ConformanceDeclaration
{
    /// <summary>
    /// List of conformance classes that this API conforms to
    /// </summary>
    [JsonPropertyName("conformsTo")]
    public required ImmutableArray<string> ConformsTo { get; init; }
}

/// <summary>
/// Link object as defined by OGC API Features specification
/// </summary>
public sealed record Link
{
    /// <summary>
    /// The URI of the linked resource
    /// </summary>
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    /// <summary>
    /// Relation type (e.g., "self", "alternate", "describedby")
    /// </summary>
    [JsonPropertyName("rel")]
    public string? Rel { get; init; }

    /// <summary>
    /// MIME type of the linked resource
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Language of the linked resource
    /// </summary>
    [JsonPropertyName("hreflang")]
    public string? HrefLang { get; init; }

    /// <summary>
    /// Human-readable title for the link
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Creates a link with required properties
    /// </summary>
    /// <param name="href">The URI of the linked resource</param>
    /// <param name="rel">Relation type</param>
    /// <param name="type">MIME type</param>
    /// <param name="title">Human-readable title</param>
    /// <returns>New link instance</returns>
    public static Link Create(string href, string? rel = null, string? type = null, string? title = null)
        => new() { Href = href, Rel = rel, Type = type, Title = title };
}

/// <summary>
/// Standard OGC API Features relation types
/// </summary>
public static class RelationTypes
{
    /// <summary>
    /// Conveys an identifier for the link's context
    /// </summary>
    public const string Self = "self";

    /// <summary>
    /// Refers to an alternate representation of the same resource
    /// </summary>
    public const string Alternate = "alternate";

    /// <summary>
    /// Refers to a resource that describes the link's context
    /// </summary>
    public const string DescribedBy = "describedby";

    /// <summary>
    /// Refers to a resource that serves as the data source
    /// </summary>
    public const string Data = "data";

    /// <summary>
    /// Indicates the link target provides service documentation
    /// </summary>
    public const string ServiceDoc = "service-doc";

    /// <summary>
    /// Indicates the link target provides service description
    /// </summary>
    public const string ServiceDesc = "service-desc";

    /// <summary>
    /// Indicates the link target provides conformance declaration
    /// </summary>
    public const string Conformance = "conformance";

    /// <summary>
    /// Indicates the link target provides collections metadata
    /// </summary>
    public const string Collections = "data";

    /// <summary>
    /// Indicates the link target provides next page of results
    /// </summary>
    public const string Next = "next";

    /// <summary>
    /// Indicates the link target provides previous page of results
    /// </summary>
    public const string Prev = "prev";
}

/// <summary>
/// Collections response containing list of available feature collections
/// </summary>
public sealed record Collections
{
    /// <summary>
    /// List of available collections
    /// </summary>
    [JsonPropertyName("collections")]
    public required ImmutableArray<CollectionInfo> CollectionList { get; init; }

    /// <summary>
    /// Links to related resources
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Feature collection metadata
/// </summary>
public sealed record CollectionInfo
{
    /// <summary>
    /// Unique identifier for the collection
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human readable title for the collection
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the collection
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Links to related resources
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    /// <summary>
    /// Spatial extent of the collection
    /// </summary>
    [JsonPropertyName("extent")]
    public Extent? Extent { get; init; }

    /// <summary>
    /// Collection data type (always "feature" for feature collections)
    /// </summary>
    [JsonPropertyName("itemType")]
    public string ItemType { get; init; } = "feature";

    /// <summary>
    /// Coordinate reference systems supported by this collection
    /// </summary>
    [JsonPropertyName("crs")]
    public ImmutableArray<string>? Crs { get; init; }
}

/// <summary>
/// Spatial and temporal extent of a collection
/// </summary>
public sealed record Extent
{
    /// <summary>
    /// Spatial extent
    /// </summary>
    [JsonPropertyName("spatial")]
    public SpatialExtent? Spatial { get; init; }

    /// <summary>
    /// Temporal extent
    /// </summary>
    [JsonPropertyName("temporal")]
    public TemporalExtent? Temporal { get; init; }
}

/// <summary>
/// Spatial extent with bounding box
/// </summary>
public sealed record SpatialExtent
{
    /// <summary>
    /// Bounding box coordinates [minx, miny, maxx, maxy]
    /// </summary>
    [JsonPropertyName("bbox")]
    public required ImmutableArray<ImmutableArray<double>> BoundingBox { get; init; }

    /// <summary>
    /// Coordinate reference system identifier
    /// </summary>
    [JsonPropertyName("crs")]
    public string Crs { get; init; } = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}

/// <summary>
/// Temporal extent with start and end times
/// </summary>
public sealed record TemporalExtent
{
    /// <summary>
    /// Temporal interval [start, end] - null values indicate open intervals
    /// </summary>
    [JsonPropertyName("interval")]
    public required ImmutableArray<ImmutableArray<string?>> Interval { get; init; }

    /// <summary>
    /// Temporal reference system
    /// </summary>
    [JsonPropertyName("trs")]
    public string Trs { get; init; } = "http://www.opengis.net/def/uom/ISO-8601/0/Gregorian";
}

/// <summary>
/// JSON converter for raw JSON strings to avoid double encoding
/// </summary>
public sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString();
    }

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
    /// Feature identifier
    /// </summary>
    [JsonPropertyName("id")]
    public object? Id { get; init; }

    /// <summary>
    /// Feature geometry in GeoJSON format
    /// </summary>
    [JsonPropertyName("geometry")]
    public SimpleGeoJsonGeometry? Geometry { get; init; }

    /// <summary>
    /// Feature properties (attributes)
    /// </summary>
    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; init; } = new();
}

/// <summary>
/// GeoJSON FeatureCollection for OGC API Features Items response
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "FeatureCollection is the standard GeoJSON type name")]
public sealed record FeatureCollection
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
}

/// <summary>
/// Standard media types for OGC API Features
/// </summary>
public static class MediaTypes
{
    /// <summary>
    /// JSON media type
    /// </summary>
    public const string Json = "application/json";

    /// <summary>
    /// GeoJSON media type
    /// </summary>
    public const string GeoJson = "application/geo+json";

    /// <summary>
    /// HTML media type
    /// </summary>
    public const string Html = "text/html";

    /// <summary>
    /// OpenAPI 3.0 specification media type
    /// </summary>
    public const string OpenApi = "application/vnd.oai.openapi+json;version=3.0";
}
