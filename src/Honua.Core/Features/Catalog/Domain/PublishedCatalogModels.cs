// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Catalog.Domain;

public sealed record PublishedCatalogProtocol
{
    public required string Protocol { get; init; }
    public required string HrefTemplate { get; init; }
    public bool PreferredPublicationRequired { get; init; }
}

public sealed record PublishedCatalogLink(string Protocol, string Href);

public sealed record PublishedCatalogItem
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string Owner { get; init; }
    public required string Access { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public string? Folder { get; init; }
    public string? ServiceId { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Layers { get; init; } = [];
    public IReadOnlyList<PublishedCatalogLink> Endpoints { get; init; } = [];
    public MetadataV2Bbox? Extent { get; init; }
    public MetadataV2TimeRange? TemporalExtent { get; init; }
}

public sealed record PublishedCatalogFacet(string Value, int Count);

public sealed record PublishedCatalogPage
{
    public required IReadOnlyList<PublishedCatalogItem> Items { get; init; }
    public required int Total { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required IReadOnlyDictionary<string, PublishedCatalogFacet[]> Facets { get; init; }
}
