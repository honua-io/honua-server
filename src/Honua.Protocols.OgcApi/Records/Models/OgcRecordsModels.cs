// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Records.Models;

/// <summary>
/// OGC API Records collections response.
/// </summary>
public sealed record OgcRecordCollectionsResponse
{
    /// <summary>
    /// Available record collections.
    /// </summary>
    [JsonPropertyName("collections")]
    public required ImmutableArray<OgcRecordCollection> Collections { get; init; }

    /// <summary>
    /// Hypermedia links.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Record collection metadata.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Collection is the OGC API resource name.")]
public sealed record OgcRecordCollection
{
    /// <summary>
    /// Stable collection identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable collection title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Human-readable collection description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Collection item type.
    /// </summary>
    [JsonPropertyName("itemType")]
    public string ItemType { get; init; } = "record";

    /// <summary>
    /// Hypermedia links.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// GeoJSON FeatureCollection containing OGC API Records record items.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "FeatureCollection is the standard GeoJSON type name.")]
public sealed record OgcRecordFeatureCollection
{
    /// <summary>
    /// GeoJSON object type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    /// <summary>
    /// Record features.
    /// </summary>
    [JsonPropertyName("features")]
    public required ImmutableArray<OgcRecordFeature> Features { get; init; }

    /// <summary>
    /// Number of records matched before pagination.
    /// </summary>
    [JsonPropertyName("numberMatched")]
    public int NumberMatched { get; init; }

    /// <summary>
    /// Number of records returned in this page.
    /// </summary>
    [JsonPropertyName("numberReturned")]
    public int NumberReturned { get; init; }

    /// <summary>
    /// Hypermedia and pagination links.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// GeoJSON Feature representing a metadata record.
/// </summary>
public sealed record OgcRecordFeature
{
    /// <summary>
    /// GeoJSON object type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    /// <summary>
    /// Stable record identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Optional GeoJSON geometry. Catalog records are currently extent-backed, so
    /// this is null and bbox carries the spatial footprint.
    /// </summary>
    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }

    /// <summary>
    /// Optional bbox in CRS84 order.
    /// </summary>
    [JsonPropertyName("bbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<double>? Bbox { get; init; }

    /// <summary>
    /// Record properties.
    /// </summary>
    [JsonPropertyName("properties")]
    public required OgcRecordProperties Properties { get; init; }

    /// <summary>
    /// Hypermedia links to the source resources and alternate protocol surfaces.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Typed record properties projected from Honua catalog resources.
/// </summary>
public sealed record OgcRecordProperties
{
    /// <summary>
    /// Human-readable record title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Human-readable record description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// OGC Records type discriminator for the resource.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Honua catalog resource type.
    /// </summary>
    [JsonPropertyName("resourceType")]
    public required string ResourceType { get; init; }

    /// <summary>
    /// Layer geometry type when the record represents a layer.
    /// </summary>
    [JsonPropertyName("geometryType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Stable external source identifiers that can be used for deterministic lookup.
    /// </summary>
    [JsonPropertyName("externalIds")]
    public required ImmutableArray<string> ExternalIds { get; init; }

    /// <summary>
    /// Visible layer identifiers when the record represents a service.
    /// </summary>
    [JsonPropertyName("layerIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<int>? LayerIds { get; init; }
}
