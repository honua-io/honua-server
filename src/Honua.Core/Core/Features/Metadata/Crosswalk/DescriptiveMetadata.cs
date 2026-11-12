// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Crosswalk;

public enum DescribedItemKind
{
    Dataset,
    Service,
    Catalog,
}

public sealed record DescriptiveDistribution(string Url, string Protocol, string? Title = null, string? MediaType = null);

// The effective descriptive metadata of one item after inheritance; every standard encoder and protocol reads this.
public sealed record DescriptiveMetadata
{
    public required string Identifier { get; init; }

    public required DescribedItemKind Kind { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public IReadOnlyList<string> Themes { get; init; } = [];

    public IReadOnlyList<string> TopicCategories { get; init; } = [];

    public string? Language { get; init; }

    public string? License { get; init; }

    public string? LicenseUrl { get; init; }

    public string AccessConstraints { get; init; } = string.Empty;

    public string Fees { get; init; } = string.Empty;

    public string? Attribution { get; init; }

    public string? Publisher { get; init; }

    public IReadOnlyList<MetadataV2ResponsibleParty> Contacts { get; init; } = [];

    public string? Lineage { get; init; }

    public string? UpdateFrequency { get; init; }

    public MetadataV2SpatialResolution? SpatialResolution { get; init; }

    public string? TemporalResolution { get; init; }

    public MetadataV2Bbox? Extent { get; init; }

    public MetadataV2TimeRange? TemporalExtent { get; init; }

    public IReadOnlyList<string> Crs { get; init; } = [];

    public IReadOnlyList<MetadataV2Link> Links { get; init; } = [];

    public IReadOnlyList<DescriptiveDistribution> Distributions { get; init; } = [];

    public required DateTimeOffset MetadataDate { get; init; }

    public MetadataV2ResponsibleParty? PointOfContact =>
        Contacts.FirstOrDefault(contact => contact.Role == MetadataV2DescriptiveCodes.PointOfContactRole)
        ?? (Contacts.Count > 0 ? Contacts[0] : null);

    public string? MetadataLink(string rel) =>
        Links.FirstOrDefault(link => string.Equals(link.Rel, rel, StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace(link.Href))?.Href;
}
