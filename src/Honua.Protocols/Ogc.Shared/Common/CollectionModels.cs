// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Common;

public sealed record Collections : ICollectionResponse<CollectionInfo>
{
    [JsonPropertyName("collections")]
    public required ImmutableArray<CollectionInfo> CollectionList { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<CollectionInfo> ICollectionResponse<CollectionInfo>.Items => CollectionList;
    ImmutableArray<ILink>? ICollectionResponse<CollectionInfo>.Links => Links.Cast<ILink>().ToImmutableArray();
    IPaginationMetadata? ICollectionResponse<CollectionInfo>.Pagination => null;
}

public sealed record CollectionInfo
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("attribution")]
    public string? Attribution { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    [JsonPropertyName("extent")]
    public Extent? Extent { get; init; }

    [JsonPropertyName("itemType")]
    public string ItemType { get; init; } = "feature";

    [JsonPropertyName("geometryType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeometryType { get; init; }

    [JsonPropertyName("crs")]
    public ImmutableArray<string>? Crs { get; init; }

    [JsonPropertyName("storageCrs")]
    public string? StorageCrs { get; init; }
}
