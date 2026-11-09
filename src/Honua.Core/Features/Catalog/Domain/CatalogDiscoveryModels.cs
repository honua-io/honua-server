// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Catalog.Domain;

public sealed record CatalogSummary(string Id, string Title, string Audience, string? PublicHref);

public sealed record CatalogView
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Audience { get; init; }
    public string? PublicHref { get; init; }
    public string? Description { get; init; }
    public required string Revision { get; init; }
}

public sealed record CatalogFolder(string Id, string Title);
public sealed record CatalogEndpoint(string Protocol, string Href);
public sealed record CatalogFacet(string Value, string Label, int Count);

public sealed record CatalogItem
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public required string Owner { get; init; }
    public required string Access { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public CatalogFolder? Folder { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public string Status { get; init; } = "active";
    public string? DatasetKind { get; init; }
    public bool Spatial { get; init; }
    [JsonConverter(typeof(CatalogExtentJsonConverter))]
    public MetadataV2Bbox? Extent { get; init; }
    public MetadataV2TimeRange? TemporalExtent { get; init; }
    public required string DetailHref { get; init; }
    public string? LaunchHref { get; init; }
    public string? ReleasedVersionId { get; init; }
    public IReadOnlyList<CatalogEndpoint> Endpoints { get; init; } = [];
    [JsonIgnore] public IReadOnlyList<string> SourceLayerIds { get; init; } = [];
    [JsonIgnore] internal IReadOnlyList<CatalogFolder> FolderContexts { get; init; } = [];
    [JsonIgnore] internal IEnumerable<CatalogFolder> VisibleFolders
        => FolderContexts.Count > 0 ? FolderContexts : Folder is { } folder ? [folder] : [];
    [JsonIgnore] public string Revision { get; init; } = string.Empty;
    [JsonIgnore] public bool CanAppearInCatalogs { get; init; } = true;
}

public sealed record CatalogPage
{
    public required string CatalogId { get; init; }
    public required string Revision { get; init; }
    public required IReadOnlyList<CatalogItem> Items { get; init; }
    public required int Total { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required IReadOnlyDictionary<string, CatalogFacet[]> Facets { get; init; }
}
