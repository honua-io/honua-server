// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
