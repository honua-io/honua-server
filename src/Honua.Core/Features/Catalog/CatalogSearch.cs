// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Catalog;

public sealed record CatalogSearch
{
    public const int MaximumPageSize = 200;
    public string? Text { get; init; }
    public IReadOnlyList<string> Kinds { get; init; } = [];
    public IReadOnlyList<string> Protocols { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Owners { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public string? FolderId { get; init; }
    public string? Status { get; init; }
    public BoundingBox? Bbox { get; init; }
    public MetadataV2TimeRange? Time { get; init; }
    public string Sort { get; init; } = "title";
    public int Offset { get; init; }
    public int Limit { get; init; } = 50;

    public CatalogPage Apply(IReadOnlyList<CatalogItem> visibleItems, CatalogView catalog)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limit, MaximumPageSize);
        var filtered = visibleItems.DistinctBy(item => (item.Kind, item.Id)).Where(Matches).ToArray();
        var ordered = Sort == "-updated" ? filtered.OrderByDescending(item => item.Updated)
            : filtered.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase);
        return new()
        {
            CatalogId = catalog.Id, Revision = catalog.Revision,
            Items = ordered.ThenBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
                .Skip(Offset).Take(Limit).ToArray(),
            Total = filtered.Length, Offset = Offset, Limit = Limit,
            Facets = new Dictionary<string, CatalogFacet[]>(StringComparer.Ordinal)
            {
                ["kind"] = Count(filtered, item => [new(item.Kind, item.Kind)]),
                ["protocol"] = Count(filtered, item => item.Endpoints.Select(endpoint => new FacetValue(endpoint.Protocol, endpoint.Protocol))),
                ["folder"] = Count(filtered, item => item.Folder is { } folder ? [new(folder.Id, folder.Title)] : []),
                ["tag"] = Count(filtered, item => item.Tags.Select(tag => new FacetValue(tag, tag))),
                ["owner"] = Count(filtered, item => [new(item.Owner, item.Owner)]),
                ["category"] = Count(filtered, item => item.Categories.Select(category => new FacetValue(category, category))),
                ["status"] = Count(filtered, item => [new(item.Status, item.Status)])
            }
        };
    }

    private bool Matches(CatalogItem item)
        => (string.IsNullOrWhiteSpace(Text) || new[] { item.Id, item.Title, item.Summary, item.Description }
                .Concat(item.Tags).Any(value => value?.Contains(Text, StringComparison.OrdinalIgnoreCase) == true))
           && Contains(Kinds, item.Kind) && Contains(Owners, item.Owner)
           && Intersects(Protocols, item.Endpoints.Select(endpoint => endpoint.Protocol))
           && Intersects(Tags, item.Tags) && Intersects(Categories, item.Categories)
           && (FolderId is null || item.Folder?.Id == FolderId)
           && (Status is null || item.Status == Status)
           && (Bbox is null || item.Extent is { } extent && Bbox.Value.Intersects(
               BoundingBox.Create(extent.West, extent.South, extent.East, extent.North, 4326)))
           && (Time is null || item.TemporalExtent is { } temporal
               && (!Time.Start.HasValue || !temporal.End.HasValue || temporal.End >= Time.Start)
               && (!Time.End.HasValue || !temporal.Start.HasValue || temporal.Start <= Time.End));

    private static bool Contains(IReadOnlyList<string> filter, string value)
        => filter.Count == 0 || filter.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool Intersects(IReadOnlyList<string> filter, IEnumerable<string> values)
        => filter.Count == 0 || filter.Intersect(values, StringComparer.OrdinalIgnoreCase).Any();

    private static CatalogFacet[] Count(IEnumerable<CatalogItem> items, Func<CatalogItem, IEnumerable<FacetValue>> values)
        => items.SelectMany(item => values(item).DistinctBy(value => value.Id, StringComparer.Ordinal))
            .GroupBy(value => value.Id, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CatalogFacet(group.Key, group.First().Label, group.Count())).ToArray();

    private sealed record FacetValue(string Id, string Label);
}
