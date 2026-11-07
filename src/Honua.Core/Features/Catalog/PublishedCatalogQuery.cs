// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Catalog;

public sealed record PublishedCatalogQuery
{
    public const int MaximumPageSize = 200;
    public string? Text { get; init; }
    public string? Type { get; init; }
    public string? Protocol { get; init; }
    public string? Folder { get; init; }
    public string? Tag { get; init; }
    public string? Owner { get; init; }
    public BoundingBox? Bbox { get; init; }
    public MetadataV2TimeRange? Time { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 50;

    public PublishedCatalogPage Apply(IReadOnlyList<PublishedCatalogItem> items)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limit, MaximumPageSize);
        var filtered = items.Where(Matches).OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Type, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        return new()
        {
            Items = filtered.Skip(Offset).Take(Limit).ToArray(), Total = filtered.Length, Offset = Offset, Limit = Limit,
            Facets = new Dictionary<string, PublishedCatalogFacet[]>(StringComparer.Ordinal)
            {
                ["type"] = Count(filtered, item => [item.Type]),
                ["protocol"] = Count(filtered, item => item.Endpoints.Select(endpoint => endpoint.Protocol)),
                ["folder"] = Count(filtered, item => item.Type != "folder" && item.Folder is { } folder ? [folder] : []),
                ["tag"] = Count(filtered, item => item.Tags),
                ["owner"] = Count(filtered, item => [item.Owner])
            }
        };
    }

    private bool Matches(PublishedCatalogItem item)
        => (Text is not { Length: > 0 } text || new[] { item.Title, item.Description, item.Id }
                .Concat(item.Tags).Any(value => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true))
            && (Type is null || Equal(Type, item.Type))
            && (Protocol is null || item.Endpoints.Any(endpoint => Equal(Protocol, endpoint.Protocol)))
            && (Folder is null || Equal(Folder, item.Folder))
            && (Tag is null || item.Tags.Any(tag => Equal(Tag, tag)))
            && (Owner is null || Equal(Owner, item.Owner))
            && (Bbox is null || item.Extent is { } extent && Bbox.Value.Intersects(
                BoundingBox.Create(extent.West, extent.South, extent.East, extent.North, 4326)))
            && (Time is null || item.TemporalExtent is { } temporal
                && (!Time.Start.HasValue || !temporal.End.HasValue || temporal.End >= Time.Start)
                && (!Time.End.HasValue || !temporal.Start.HasValue || temporal.Start <= Time.End));

    private static bool Equal(string left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static PublishedCatalogFacet[] Count(IEnumerable<PublishedCatalogItem> items, Func<PublishedCatalogItem, IEnumerable<string>> values)
        => items.SelectMany(item => values(item).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PublishedCatalogFacet(group.Key, group.Count())).ToArray();
}
