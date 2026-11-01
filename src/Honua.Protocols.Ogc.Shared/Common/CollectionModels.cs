// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// Collections response containing list of available feature collections.
/// </summary>
public sealed record Collections : ICollectionResponse<CollectionInfo>
{
    /// <summary>
    /// List of available collections.
    /// </summary>
    [JsonPropertyName("collections")]
    public required ImmutableArray<CollectionInfo> CollectionList { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<CollectionInfo> ICollectionResponse<CollectionInfo>.Items => CollectionList;
    ImmutableArray<ILink>? ICollectionResponse<CollectionInfo>.Links => Links.Cast<ILink>().ToImmutableArray();
    IPaginationMetadata? ICollectionResponse<CollectionInfo>.Pagination => null;
}

/// <summary>
/// Feature collection metadata.
/// </summary>
public sealed record CollectionInfo
{
    /// <summary>
    /// Unique identifier for the collection.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human readable title for the collection.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the collection.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Human-readable data attribution from canonical metadata.</summary>
    [JsonPropertyName("attribution")]
    public string? Attribution { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    /// <summary>
    /// Spatial extent of the collection.
    /// </summary>
    [JsonPropertyName("extent")]
    public Extent? Extent { get; init; }

    /// <summary>
    /// Collection data type (always "feature" for feature collections).
    /// </summary>
    [JsonPropertyName("itemType")]
    public string ItemType { get; init; } = "feature";

    /// <summary>
    /// Canonical geometry type (for example Point or Polygon). GeometryCollection
    /// denotes a generic or heterogeneous spatial resource; absent for non-spatial resources.
    /// </summary>
    [JsonPropertyName("geometryType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Coordinate reference systems supported by this collection.
    /// </summary>
    [JsonPropertyName("crs")]
    public ImmutableArray<string>? Crs { get; init; }

    /// <summary>
    /// Storage coordinate reference system identifier.
    /// </summary>
    [JsonPropertyName("storageCrs")]
    public string? StorageCrs { get; init; }
}
