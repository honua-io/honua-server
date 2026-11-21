// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Styles.Models;

public sealed class OgcStylesConformance
{
    [JsonPropertyName("conformsTo")]
    public required string[] ConformsTo { get; init; }
}

public sealed record StylesList
{
    [JsonPropertyName("styles")]
    public required ImmutableArray<StyleEntry> Styles { get; init; }

    [JsonPropertyName("default")]
    public string? Default { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }
}

public sealed record StyleEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

public sealed record StyleMetadataResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("keywords")]
    public ImmutableArray<string>? Keywords { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

public sealed record SharedStyleAssociationResponse
{
    [JsonPropertyName("resourceId")]
    public required string ResourceId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }
}

public sealed record SharedStyleConsumerResponse
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [JsonPropertyName("contentHash")]
    public required string ContentHash { get; init; }

    [JsonPropertyName("publicationState")]
    public required string PublicationState { get; init; }

    [JsonPropertyName("versionId")]
    public string? VersionId { get; init; }

    [JsonPropertyName("draftId")]
    public string? DraftId { get; init; }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    [JsonPropertyName("isCurrent")]
    public bool IsCurrent { get; init; }
}

public sealed record SharedStyleImpactResponse
{
    [JsonPropertyName("styleId")]
    public required string StyleId { get; init; }

    [JsonPropertyName("styleVersion")]
    public required int StyleVersion { get; init; }

    [JsonPropertyName("etag")]
    public required string Etag { get; init; }

    [JsonPropertyName("contentHash")]
    public required string ContentHash { get; init; }

    [JsonPropertyName("impactHash")]
    public required string ImpactHash { get; init; }

    [JsonPropertyName("associations")]
    public required SharedStyleAssociationResponse[] Associations { get; init; }

    [JsonPropertyName("consumers")]
    public required SharedStyleConsumerResponse[] Consumers { get; init; }

    [JsonPropertyName("canEdit")]
    public required bool CanEdit { get; init; }

    [JsonPropertyName("impactComplete")]
    public required bool ImpactComplete { get; init; }
}
