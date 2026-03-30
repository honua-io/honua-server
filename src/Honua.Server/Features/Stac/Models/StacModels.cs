// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Ogc.Common;

namespace Honua.Server.Features.Stac.Models;

/// <summary>
/// STAC Catalog root object per the STAC API specification.
/// </summary>
public sealed record StacCatalog
{
    /// <summary>
    /// STAC specification version.
    /// </summary>
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = StacConstants.StacVersion;

    /// <summary>
    /// Type identifier — always "Catalog" for the root.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Catalog";

    /// <summary>
    /// Catalog identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the catalog contents.
    /// </summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// Conformance classes supported by this STAC API.
    /// </summary>
    [JsonPropertyName("conformsTo")]
    public ImmutableArray<string>? ConformsTo { get; init; }

    /// <summary>
    /// Hypermedia links.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// STAC Collection with extent, provider, and license metadata.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "STAC specification type name")]
public sealed record StacCollection
{
    /// <summary>
    /// STAC specification version.
    /// </summary>
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = StacConstants.StacVersion;

    /// <summary>
    /// Type identifier.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Collection";

    /// <summary>
    /// Collection identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the collection.
    /// </summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// Keywords for discovery.
    /// </summary>
    [JsonPropertyName("keywords")]
    public ImmutableArray<string>? Keywords { get; init; }

    /// <summary>
    /// License identifier (SPDX or "proprietary" / "various").
    /// </summary>
    [JsonPropertyName("license")]
    public required string License { get; init; }

    /// <summary>
    /// Spatial and temporal extent.
    /// </summary>
    [JsonPropertyName("extent")]
    public required StacExtent Extent { get; init; }

    /// <summary>
    /// Hypermedia links.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    /// <summary>
    /// STAC extensions in use.
    /// </summary>
    [JsonPropertyName("stac_extensions")]
    public ImmutableArray<string>? StacExtensions { get; init; }
}

/// <summary>
/// STAC Item — a GeoJSON Feature with STAC metadata.
/// </summary>
public sealed record StacItem
{
    /// <summary>
    /// STAC specification version.
    /// </summary>
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = StacConstants.StacVersion;

    /// <summary>
    /// Type identifier — always "Feature".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    /// <summary>
    /// Item identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// GeoJSON geometry.
    /// </summary>
    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }

    /// <summary>
    /// Bounding box [minx, miny, maxx, maxy].
    /// </summary>
    [JsonPropertyName("bbox")]
    public ImmutableArray<double>? Bbox { get; init; }

    /// <summary>
    /// STAC properties including required datetime.
    /// </summary>
    [JsonPropertyName("properties")]
    public required Dictionary<string, object?> Properties { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    /// <summary>
    /// Asset dictionary keyed by role.
    /// </summary>
    [JsonPropertyName("assets")]
    public Dictionary<string, StacAsset>? Assets { get; init; }

    /// <summary>
    /// Parent collection identifier.
    /// </summary>
    [JsonPropertyName("collection")]
    public string? Collection { get; init; }

    /// <summary>
    /// STAC extensions in use.
    /// </summary>
    [JsonPropertyName("stac_extensions")]
    public ImmutableArray<string>? StacExtensions { get; init; }
}

/// <summary>
/// Asset with href and media type for a STAC item.
/// </summary>
public sealed record StacAsset
{
    /// <summary>
    /// URI to the asset.
    /// </summary>
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    /// <summary>
    /// Human-readable title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Asset description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Media type of the asset.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Semantic roles (e.g., "data", "thumbnail", "metadata").
    /// </summary>
    [JsonPropertyName("roles")]
    public ImmutableArray<string>? Roles { get; init; }
}

/// <summary>
/// STAC spatial and temporal extent.
/// </summary>
public sealed record StacExtent
{
    /// <summary>
    /// Spatial extent with bounding box(es).
    /// </summary>
    [JsonPropertyName("spatial")]
    public required StacSpatialExtent Spatial { get; init; }

    /// <summary>
    /// Temporal extent with interval(s).
    /// </summary>
    [JsonPropertyName("temporal")]
    public required StacTemporalExtent Temporal { get; init; }
}

/// <summary>
/// Spatial extent as one or more bounding boxes.
/// </summary>
public sealed record StacSpatialExtent
{
    /// <summary>
    /// Bounding boxes in CRS84 [west, south, east, north].
    /// </summary>
    [JsonPropertyName("bbox")]
    public required ImmutableArray<ImmutableArray<double>> Bbox { get; init; }
}

/// <summary>
/// Temporal extent as one or more intervals.
/// </summary>
public sealed record StacTemporalExtent
{
    /// <summary>
    /// Temporal intervals [[start, end], ...] — null values indicate open bounds.
    /// </summary>
    [JsonPropertyName("interval")]
    public required ImmutableArray<ImmutableArray<string?>> Interval { get; init; }
}

/// <summary>
/// STAC search request body for POST /stac/search.
/// </summary>
public sealed record StacSearchRequest
{
    /// <summary>
    /// Bounding box filter [west, south, east, north].
    /// </summary>
    [JsonPropertyName("bbox")]
    public ImmutableArray<double>? Bbox { get; init; }

    /// <summary>
    /// GeoJSON geometry for spatial intersection.
    /// </summary>
    [JsonPropertyName("intersects")]
    public JsonElement? Intersects { get; init; }

    /// <summary>
    /// RFC 3339 datetime or interval ("../end", "start/..", "start/end").
    /// </summary>
    [JsonPropertyName("datetime")]
    public string? Datetime { get; init; }

    /// <summary>
    /// Collection IDs to search within.
    /// </summary>
    [JsonPropertyName("collections")]
    public ImmutableArray<string>? Collections { get; init; }

    /// <summary>
    /// Item IDs to filter by.
    /// </summary>
    [JsonPropertyName("ids")]
    public ImmutableArray<string>? Ids { get; init; }

    /// <summary>
    /// Maximum number of items to return.
    /// </summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>
    /// Fields extension: include/exclude field lists.
    /// </summary>
    [JsonPropertyName("fields")]
    public StacFieldsExtension? Fields { get; init; }

    /// <summary>
    /// Sort extension: ordering specification.
    /// </summary>
    [JsonPropertyName("sortby")]
    public ImmutableArray<StacSortDefinition>? Sortby { get; init; }

    /// <summary>
    /// CQL2 filter expression (query extension).
    /// </summary>
    [JsonPropertyName("filter")]
    public JsonElement? Filter { get; init; }

    /// <summary>
    /// CRS for the filter geometry or filter expression.
    /// </summary>
    [JsonPropertyName("filter-crs")]
    public string? FilterCrs { get; init; }

    /// <summary>
    /// Filter language (defaults to cql2-json for POST).
    /// </summary>
    [JsonPropertyName("filter-lang")]
    public string? FilterLang { get; init; }
}

/// <summary>
/// Fields extension for selecting properties to include/exclude.
/// </summary>
public sealed record StacFieldsExtension
{
    /// <summary>
    /// Properties to include.
    /// </summary>
    [JsonPropertyName("include")]
    public ImmutableArray<string>? Includes { get; init; }

    /// <summary>
    /// Properties to exclude.
    /// </summary>
    [JsonPropertyName("exclude")]
    public ImmutableArray<string>? Excludes { get; init; }
}

/// <summary>
/// Sort definition for the sort extension.
/// </summary>
public sealed record StacSortDefinition
{
    /// <summary>
    /// Property name to sort by.
    /// </summary>
    [JsonPropertyName("field")]
    public required string Field { get; init; }

    /// <summary>
    /// Sort direction: "asc" or "desc".
    /// </summary>
    [JsonPropertyName("direction")]
    public string Direction { get; init; } = "asc";
}

/// <summary>
/// STAC Item Collection (search response).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "STAC specification type name")]
public sealed record StacItemCollection
{
    /// <summary>
    /// Type identifier.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    /// <summary>
    /// Matched items.
    /// </summary>
    [JsonPropertyName("features")]
    public required ImmutableArray<StacItem> Features { get; init; }

    /// <summary>
    /// Hypermedia links (pagination).
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    /// <summary>
    /// Context with matched/returned/limit counts.
    /// </summary>
    [JsonPropertyName("context")]
    public StacSearchContext? Context { get; init; }

    /// <summary>
    /// Total number of matched items (numberMatched).
    /// </summary>
    [JsonPropertyName("numberMatched")]
    public long? NumberMatched { get; init; }

    /// <summary>
    /// Number of items returned.
    /// </summary>
    [JsonPropertyName("numberReturned")]
    public int? NumberReturned { get; init; }
}

/// <summary>
/// Search context with matched/returned/limit counts.
/// </summary>
public sealed record StacSearchContext
{
    /// <summary>
    /// Number of items returned in this response.
    /// </summary>
    [JsonPropertyName("returned")]
    public required int Returned { get; init; }

    /// <summary>
    /// Total number of items matching the query.
    /// </summary>
    [JsonPropertyName("matched")]
    public long? Matched { get; init; }

    /// <summary>
    /// Maximum number of items requested.
    /// </summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

/// <summary>
/// Response wrapper for the STAC collections list.
/// </summary>
public sealed record StacCollectionsResponse
{
    /// <summary>
    /// Available collections.
    /// </summary>
    [JsonPropertyName("collections")]
    public required ImmutableArray<StacCollection> Collections { get; init; }

    /// <summary>
    /// Hypermedia links.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}
