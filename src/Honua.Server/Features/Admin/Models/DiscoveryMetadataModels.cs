// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

public sealed record DiscoveryMetadataUpdateRequest
{
    public required string Etag { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string[] Keywords { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement>? CustomFields { get; init; }
}

public sealed record DiscoveryMetadataResponse
{
    public required string Etag { get; init; }
    public required string EntityId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<DiscoveryPublicationImpact> AffectedPublications { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> CustomFields { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<MetadataV2CustomFieldSchema> CustomFieldSchemas { get; init; } = [];
    public IReadOnlyList<MetadataFieldHistoryEntry> RecentHistory { get; init; } = [];
}

public sealed record DiscoveryPublicationImpact(string PublicationId, string ServiceName, string Identifier)
{
    public int? PreviewStorageLayerId { get; init; }
}

public sealed record MetadataHistoryItemQuery
{
    public string? ItemKind { get; init; }
    public string? ItemId { get; init; }
}

public sealed record MetadataHistoryPageQuery
{
    private const int DefaultLimit = 50;

    public string? Field { get; init; }
    public long? BeforeId { get; init; }
    public int? Limit { get; init; }

    public string? Error => Limit is <= 0 ? "limit must be a positive integer."
        : BeforeId is <= 0 ? "beforeId must be a positive integer."
        : null;

    public MetadataFieldHistoryQuery ToQuery(string? itemKind, string? itemId) => new()
    {
        ItemKind = itemKind,
        ItemId = itemId,
        FieldPrefix = Field,
        BeforeId = BeforeId,
        Limit = Limit ?? DefaultLimit,
    };
}

public sealed record MetadataFieldHistoryResponse
{
    public required IReadOnlyList<MetadataFieldHistoryEntry> Items { get; init; }
    public long? NextBeforeId { get; init; }
    public bool HasMore { get; init; }
}

public sealed record MetadataCustomFieldSchemasResponse
{
    public required string Etag { get; init; }
    public required string Environment { get; init; }
    public IReadOnlyList<MetadataV2CustomFieldSchema> Schemas { get; init; } = [];
}

public sealed record MetadataCustomFieldSchemasUpdateRequest
{
    public required string Etag { get; init; }
    public MetadataV2CustomFieldSchema[] Schemas { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiscoveryMetadataUpdateRequest))]
[JsonSerializable(typeof(ApiResponse<DiscoveryMetadataResponse>))]
[JsonSerializable(typeof(ApiResponse<MetadataFieldHistoryResponse>))]
[JsonSerializable(typeof(ApiResponse<MetadataCustomFieldSchemasResponse>))]
[JsonSerializable(typeof(MetadataCustomFieldSchemasUpdateRequest))]
public sealed partial class DiscoveryMetadataJsonContext : JsonSerializerContext;
