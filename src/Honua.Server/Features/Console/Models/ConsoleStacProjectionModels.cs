// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Console.Models;

/// <summary>
/// Minimal STAC link object (rel/href) used across the Console open-data STAC
/// projections (catalog, collection, item).
/// </summary>
public sealed class StacProjectionLink
{
    /// <summary>Link relation type (e.g. <c>self</c>, <c>root</c>, <c>child</c>, <c>item</c>).</summary>
    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    /// <summary>Link target.</summary>
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    /// <summary>Optional media type of the target.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Optional human-readable link title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

/// <summary>
/// STAC asset object (distribution link).
/// </summary>
public sealed class StacProjectionAsset
{
    /// <summary>Asset href.</summary>
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    /// <summary>Asset title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Asset media type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Asset roles (e.g. <c>data</c>).</summary>
    [JsonPropertyName("roles")]
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>
/// STAC Catalog projection over the published open-data items. This is the
/// anonymous catalog-root projection a STAC client crawls.
/// </summary>
public sealed class StacProjectionCatalog
{
    /// <summary>STAC spec version.</summary>
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = "1.0.0";

    /// <summary>STAC entity type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Catalog";

    /// <summary>Catalog id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Catalog title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Catalog description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Navigation links (self/root + a child link per published collection).</summary>
    [JsonPropertyName("links")]
    public required IReadOnlyList<StacProjectionLink> Links { get; init; }
}

/// <summary>
/// STAC Collection projection for a single published open-data item.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "STAC specification type name")]
public sealed class StacProjectionCollection
{
    /// <summary>STAC spec version.</summary>
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = "1.0.0";

    /// <summary>STAC entity type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Collection";

    /// <summary>Collection id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Collection title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Collection description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>License URL/SPDX identifier (STAC requires a value; defaults to <c>other</c>).</summary>
    [JsonPropertyName("license")]
    public required string License { get; init; }

    /// <summary>Discovery keywords.</summary>
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary>Spatial + temporal extents.</summary>
    [JsonPropertyName("extent")]
    public required StacProjectionExtent Extent { get; init; }

    /// <summary>Navigation links.</summary>
    [JsonPropertyName("links")]
    public required IReadOnlyList<StacProjectionLink> Links { get; init; }

    /// <summary>Distribution assets keyed by asset key.</summary>
    [JsonPropertyName("assets")]
    public IReadOnlyDictionary<string, StacProjectionAsset>? Assets { get; init; }
}

/// <summary>
/// STAC collection extent (spatial bbox + temporal interval).
/// </summary>
public sealed class StacProjectionExtent
{
    /// <summary>Spatial extent.</summary>
    [JsonPropertyName("spatial")]
    public required StacProjectionSpatialExtent Spatial { get; init; }

    /// <summary>Temporal extent.</summary>
    [JsonPropertyName("temporal")]
    public required StacProjectionTemporalExtent Temporal { get; init; }
}

/// <summary>STAC spatial extent: an array of bbox arrays (W,S,E,N).</summary>
public sealed class StacProjectionSpatialExtent
{
    /// <summary>Bounding boxes; the first is the overall extent.</summary>
    [JsonPropertyName("bbox")]
    public required IReadOnlyList<IReadOnlyList<double>> Bbox { get; init; }
}

/// <summary>STAC temporal extent: an array of [start, end] RFC-3339 interval arrays.</summary>
public sealed class StacProjectionTemporalExtent
{
    /// <summary>Intervals; null bounds denote open-ended.</summary>
    [JsonPropertyName("interval")]
    public required IReadOnlyList<IReadOnlyList<string?>> Interval { get; init; }
}

/// <summary>
/// STAC Item projection for a published open-data item. Honua emits one
/// representative item per published collection (the dataset itself); per-feature
/// item generation is a post-MVP enhancement.
/// </summary>
public sealed class StacProjectionItem
{
    /// <summary>STAC spec version.</summary>
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = "1.0.0";

    /// <summary>STAC entity type (GeoJSON Feature).</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    /// <summary>Item id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Owning collection id.</summary>
    [JsonPropertyName("collection")]
    public required string Collection { get; init; }

    /// <summary>Item bounding box (W,S,E,N).</summary>
    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; init; }

    /// <summary>Item geometry (GeoJSON). Null when no spatial extent is known.</summary>
    [JsonPropertyName("geometry")]
    public StacProjectionGeometry? Geometry { get; init; }

    /// <summary>Common metadata properties (datetime, title, etc.).</summary>
    [JsonPropertyName("properties")]
    public required IReadOnlyDictionary<string, string?> Properties { get; init; }

    /// <summary>Navigation links.</summary>
    [JsonPropertyName("links")]
    public required IReadOnlyList<StacProjectionLink> Links { get; init; }

    /// <summary>Distribution assets keyed by asset key.</summary>
    [JsonPropertyName("assets")]
    public IReadOnlyDictionary<string, StacProjectionAsset>? Assets { get; init; }
}

/// <summary>
/// GeoJSON Polygon geometry derived from the dataset bbox.
/// </summary>
public sealed class StacProjectionGeometry
{
    /// <summary>GeoJSON geometry type. Honua emits a bbox-derived Polygon.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Polygon";

    /// <summary>Polygon coordinate rings.</summary>
    [JsonPropertyName("coordinates")]
    public required IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> Coordinates { get; init; }
}

/// <summary>
/// Schema.org <c>Dataset</c> JSON-LD projection for Console open-data page
/// preview. Emitted only where the underlying fields are available.
/// </summary>
public sealed class SchemaOrgDataset
{
    /// <summary>JSON-LD context.</summary>
    [JsonPropertyName("@context")]
    public string Context { get; init; } = "https://schema.org";

    /// <summary>Schema.org type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Dataset";

    /// <summary>Dataset name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Dataset description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Keywords.</summary>
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary>License URL/identifier.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Landing page URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Spatial coverage as a GeoShape box string (S W N E lat/lon pairs).</summary>
    [JsonPropertyName("spatialCoverage")]
    public SchemaOrgPlace? SpatialCoverage { get; init; }

    /// <summary>Temporal coverage as an ISO-8601 interval string.</summary>
    [JsonPropertyName("temporalCoverage")]
    public string? TemporalCoverage { get; init; }

    /// <summary>Publisher organization.</summary>
    [JsonPropertyName("publisher")]
    public SchemaOrgOrganization? Publisher { get; init; }

    /// <summary>Distributions.</summary>
    [JsonPropertyName("distribution")]
    public IReadOnlyList<SchemaOrgDataDownload>? Distribution { get; init; }
}

/// <summary>Schema.org <c>Place</c> with a bounding-box geo shape.</summary>
public sealed class SchemaOrgPlace
{
    /// <summary>Schema.org type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Place";

    /// <summary>Bounding-box geo shape.</summary>
    [JsonPropertyName("geo")]
    public required SchemaOrgGeoShape Geo { get; init; }
}

/// <summary>Schema.org <c>GeoShape</c> box.</summary>
public sealed class SchemaOrgGeoShape
{
    /// <summary>Schema.org type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "GeoShape";

    /// <summary>Box as "minLat minLon maxLat maxLon".</summary>
    [JsonPropertyName("box")]
    public required string Box { get; init; }
}

/// <summary>Schema.org <c>Organization</c>.</summary>
public sealed class SchemaOrgOrganization
{
    /// <summary>Schema.org type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Organization";

    /// <summary>Organization name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>Schema.org <c>DataDownload</c>.</summary>
public sealed class SchemaOrgDataDownload
{
    /// <summary>Schema.org type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "DataDownload";

    /// <summary>Download/access URL.</summary>
    [JsonPropertyName("contentUrl")]
    public required string ContentUrl { get; init; }

    /// <summary>Encoding/media type.</summary>
    [JsonPropertyName("encodingFormat")]
    public string? EncodingFormat { get; init; }

    /// <summary>Distribution name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
