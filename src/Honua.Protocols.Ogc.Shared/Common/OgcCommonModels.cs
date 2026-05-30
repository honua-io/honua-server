// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.Ogc.Common;

/// <summary>
/// OGC API landing page response.
/// </summary>
public sealed record LandingPage
{
    /// <summary>
    /// Title of the API.
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Description of the API.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Indicates whether 3D coordinates are supported.
    /// </summary>
    [JsonPropertyName("x-honua-3d-supported")]
    public bool? Supports3d { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Conformance declaration response.
/// </summary>
public sealed record ConformanceDeclaration
{
    /// <summary>
    /// List of conformance classes that this API conforms to.
    /// </summary>
    [JsonPropertyName("conformsTo")]
    public required ImmutableArray<string> ConformsTo { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }
}

/// <summary>
/// Link object as defined by OGC API specifications.
/// </summary>
public sealed record Link : ILink
{
    /// <summary>
    /// The URI of the linked resource.
    /// </summary>
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    /// <summary>
    /// Relation type (e.g., "self", "alternate", "describedby").
    /// </summary>
    [JsonPropertyName("rel")]
    public string? Rel { get; init; }

    /// <summary>
    /// MIME type of the linked resource.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Indicates the link is a URI template.
    /// </summary>
    [JsonPropertyName("templated")]
    public bool? Templated { get; init; }

    /// <summary>
    /// Language of the linked resource.
    /// </summary>
    [JsonPropertyName("hreflang")]
    public string? HrefLang { get; init; }

    /// <summary>
    /// Human-readable title for the link.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Creates a link with required properties.
    /// </summary>
    /// <param name="href">The URI of the linked resource.</param>
    /// <param name="rel">Relation type.</param>
    /// <param name="type">MIME type.</param>
    /// <param name="title">Human-readable title.</param>
    /// <returns>New link instance.</returns>
    public static Link Create(string href, string? rel = null, string? type = null, string? title = null)
        => new() { Href = href, Rel = rel, Type = type, Title = title };
}

/// <summary>
/// Standard OGC relation types.
/// </summary>
public static class RelationTypes
{
    /// <summary>
    /// Conveys an identifier for the link's context.
    /// </summary>
    public const string Self = "self";

    /// <summary>
    /// Refers to an alternate representation of the same resource.
    /// </summary>
    public const string Alternate = "alternate";

    /// <summary>
    /// Refers to a resource that describes the link's context.
    /// </summary>
    public const string DescribedBy = "describedby";

    /// <summary>
    /// Refers to a resource that serves as the data source.
    /// </summary>
    public const string Data = "data";

    /// <summary>
    /// Refers to the items (features) of a collection.
    /// </summary>
    public const string Items = "items";

    /// <summary>
    /// Refers to a style resource for the collection.
    /// </summary>
    public const string Style = "style";

    /// <summary>
    /// Indicates the link target provides service documentation.
    /// </summary>
    public const string ServiceDoc = "service-doc";

    /// <summary>
    /// Indicates the link target provides service description.
    /// </summary>
    public const string ServiceDesc = "service-desc";

    /// <summary>
    /// Indicates the link target provides conformance declaration.
    /// </summary>
    public const string Conformance = "conformance";

    /// <summary>
    /// Indicates the link target provides the collection containing a feature.
    /// </summary>
    public const string Collection = "collection";

    /// <summary>
    /// Indicates the link target provides next page of results.
    /// </summary>
    public const string Next = "next";

    /// <summary>
    /// Indicates the link target provides previous page of results.
    /// </summary>
    public const string Prev = "prev";

    /// <summary>
    /// Indicates the link target provides queryables schema.
    /// </summary>
    public const string Queryables = "http://www.opengis.net/def/rel/ogc/1.0/queryables";

    /// <summary>
    /// Indicates the link target provides a JSON Schema document.
    /// </summary>
    public const string Schema = "http://www.opengis.net/def/rel/ogc/1.0/schema";

    /// <summary>
    /// Indicates the link target provides a coverage representation.
    /// </summary>
    public const string Coverage = "http://www.opengis.net/def/rel/ogc/1.0/coverage";

    /// <summary>
    /// Indicates the link target provides a tilesets list for vector tiles.
    /// </summary>
    public const string TilesetsVector = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-vector";

    /// <summary>
    /// Indicates the link target provides a tilesets list for map tiles.
    /// </summary>
    public const string TilesetsMap = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-map";

    /// <summary>
    /// Indicates the link target provides a map representation of a geospatial resource.
    /// </summary>
    public const string Map = "https://www.opengis.net/def/rel/ogc/1.0/map";

    /// <summary>
    /// Indicates the link target provides a tilesets list for coverage tiles.
    /// </summary>
    public const string TilesetsCoverage = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-coverage";

    /// <summary>
    /// Indicates the link target provides the tiling scheme definition.
    /// </summary>
    public const string TilingScheme = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme";

    /// <summary>
    /// Indicates the link target provides the geospatial data resource.
    /// </summary>
    public const string Geodata = "http://www.opengis.net/def/rel/ogc/1.0/geodata";

    /// <summary>
    /// Indicates the link target provides the results of a completed OGC API
    /// Processes job (Part 1 §7.11.1).
    /// </summary>
    public const string OgcResults = "http://www.opengis.net/def/rel/ogc/1.0/results";
}

/// <summary>
/// Spatial and temporal extent of a collection.
/// </summary>
public sealed record Extent
{
    /// <summary>
    /// Spatial extent.
    /// </summary>
    [JsonPropertyName("spatial")]
    public SpatialExtent? Spatial { get; init; }

    /// <summary>
    /// Temporal extent.
    /// </summary>
    [JsonPropertyName("temporal")]
    public TemporalExtent? Temporal { get; init; }
}

/// <summary>
/// Spatial extent with bounding box.
/// </summary>
public sealed record SpatialExtent
{
    /// <summary>
    /// Bounding box coordinates [minx, miny, maxx, maxy].
    /// </summary>
    [JsonPropertyName("bbox")]
    public required ImmutableArray<ImmutableArray<double>> BoundingBox { get; init; }

    /// <summary>
    /// Bounding box coordinates expressed in the collection storage CRS.
    /// </summary>
    [JsonPropertyName("storageCrsBbox")]
    public ImmutableArray<ImmutableArray<double>>? StorageCrsBoundingBox { get; init; }

    /// <summary>
    /// Coordinate reference system identifier.
    /// </summary>
    [JsonPropertyName("crs")]
    public string Crs { get; init; } = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}

/// <summary>
/// Temporal extent with start and end times.
/// </summary>
public sealed record TemporalExtent
{
    /// <summary>
    /// Temporal interval [start, end] - null values indicate open intervals.
    /// </summary>
    [JsonPropertyName("interval")]
    public required ImmutableArray<ImmutableArray<string?>> Interval { get; init; }

    /// <summary>
    /// Temporal reference system.
    /// </summary>
    [JsonPropertyName("trs")]
    public string Trs { get; init; } = "http://www.opengis.net/def/uom/ISO-8601/0/Gregorian";
}

/// <summary>
/// Standard media types for OGC APIs.
/// </summary>
public static class MediaTypes
{
    /// <summary>
    /// JSON media type.
    /// </summary>
    public const string Json = "application/json";

    /// <summary>
    /// GeoJSON media type.
    /// </summary>
    public const string GeoJson = "application/geo+json";

    /// <summary>
    /// HTML media type.
    /// </summary>
    public const string Html = "text/html";

    /// <summary>
    /// CSV media type.
    /// </summary>
    public const string Csv = "text/csv";

    /// <summary>
    /// OpenAPI 3.0 specification media type.
    /// </summary>
    public const string OpenApi = "application/vnd.oai.openapi+json;version=3.0";

    /// <summary>
    /// JSON Schema media type.
    /// </summary>
    public const string SchemaJson = "application/schema+json";

    /// <summary>
    /// GML 3.2 media type.
    /// </summary>
    public const string Gml = "application/gml+xml;version=3.2";

    /// <summary>
    /// Mapbox Vector Tile media type.
    /// </summary>
    public const string Mvt = "application/vnd.mapbox-vector-tile";

    /// <summary>
    /// PNG image media type.
    /// </summary>
    public const string Png = "image/png";
}
