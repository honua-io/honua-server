// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Crosswalk;

// Descriptive fields (title, description, keywords, themes, lineage...) belong to the item itself; a layer may
// override its dataset's. Governance fields (license, access constraints, fees, attribution, contacts, language,
// governance links) fall back from the item to its service, then the service's folder, then the environment.
// A dataset never inherits a publisher; a service takes its folder's, then the environment's.
public static class DescriptiveMetadataResolver
{
    private const int Wgs84Srid = 4326;

    public sealed record DatasetContext(
        MetadataV2Resource Resource, MetadataV2Service? Service, MetadataV2Publication? Layer = null);

    private sealed record DescriptionSource(
        DescribedItemKind Kind, MetadataV2ObjectMetadata[] Own, MetadataV2ObjectMetadata Governance);

    public static DescriptiveMetadata ForDataset(MetadataV2Graph graph, DatasetContext context)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(context);
        var (resource, service, layer) = context;
        ArgumentNullException.ThrowIfNull(resource);
        MetadataV2ObjectMetadata[] own = layer is null ? [resource.Metadata] : [layer.Metadata, resource.Metadata];
        var governance = GovernanceFor(graph, resource, service);
        return Describe(graph, new DescriptionSource(DescribedItemKind.Dataset, own, governance)) with
        {
            Title = First(own, metadata => metadata.Title) ?? resource.Metadata.Name,
            Publisher = Text(resource.Metadata.Publisher),
            Extent = resource.ReadSrid() is null or Wgs84Srid ? resource.ReadBbox() : null,
            TemporalExtent = resource.Temporal?.Extent,
            Crs = SupportedCrs(resource),
        };
    }

    public static DescriptiveMetadata ForService(MetadataV2Graph graph, MetadataV2Service service)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(service);
        MetadataV2ObjectMetadata[] chain = [service.Metadata, .. FolderChain(graph, service)];
        return Describe(graph, new DescriptionSource(DescribedItemKind.Service, [service.Metadata], Fold(chain))) with
        {
            Title = Text(service.Metadata.Title) ?? service.EffectivePublicName,
            Publisher = First(chain, metadata => metadata.Publisher),
        };
    }

    // Capabilities documents describe the service as a whole, so an untitled service also takes its folder's,
    // then the environment's, title, abstract and keywords. Catalogue records keep
    // the service's own text so untitled services stay distinguishable.
    public static DescriptiveMetadata ForCapabilities(MetadataV2Graph graph, MetadataV2Service service)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(service);
        MetadataV2ObjectMetadata[] chain = [service.Metadata, .. FolderChain(graph, service)];
        return ForService(graph, service) with
        {
            Title = First(chain, metadata => metadata.Title) ?? service.EffectivePublicName,
            Description = First(chain, metadata => metadata.Description),
            Keywords = Terms(chain, metadata => metadata.Keywords),
        };
    }

    public static DescriptiveMetadata ForCatalog(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return Describe(graph, new DescriptionSource(DescribedItemKind.Catalog, [graph.Metadata], graph.Metadata)) with
        {
            Title = Text(graph.Metadata.Title) ?? Text(graph.Environment) ?? "Honua",
            Publisher = Text(graph.Metadata.Publisher),
        };
    }

    // The dataset's governance after service, folder and environment fallbacks, for emitters that project the
    // metadata record directly.
    public static MetadataV2ObjectMetadata GovernanceFor(MetadataV2Graph graph, MetadataV2Resource resource, MetadataV2Service? service)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resource);
        MetadataV2ObjectMetadata[] parents = service is null ? [graph.Metadata] : [service.Metadata, .. FolderChain(graph, service)];
        return Fold([resource.Metadata, .. parents]);
    }

    public static string AccessStatementFor(string? license)
    {
        if (string.IsNullOrWhiteSpace(license))
        {
            return string.Empty;
        }
        return SpdxLicensePolicy.GetLicenseUrl(license) is { } url
            ? $"License: {license} ({url})"
            : $"License: {license}";
    }

    private static DescriptiveMetadata Describe(MetadataV2Graph graph, DescriptionSource source)
    {
        var (kind, own, governance) = source;
        var item = own[0];
        var license = Text(governance.License);
        return new DescriptiveMetadata
        {
            Identifier = item.Id,
            Kind = kind,
            Title = item.Name,
            Description = First(own, metadata => metadata.Description),
            Keywords = Terms(own, metadata => metadata.Keywords),
            Themes = Terms(own, metadata => metadata.Themes),
            TopicCategories = FirstList(own, metadata => metadata.TopicCategories),
            Language = Text(governance.Language),
            License = license,
            LicenseUrl = Text(governance.Links.FirstOrDefault(link =>
                string.Equals(link.Rel, "license", StringComparison.OrdinalIgnoreCase))?.Href)
                ?? (license is null ? null : SpdxLicensePolicy.GetLicenseUrl(license)),
            AccessConstraints = Text(governance.AccessConstraints) ?? AccessStatementFor(license),
            Fees = Text(governance.Fees) ?? string.Empty,
            Attribution = Text(governance.Attribution),
            Contacts = ContactsOf(governance),
            Lineage = First(own, metadata => metadata.Lineage),
            UpdateFrequency = First(own, metadata => metadata.UpdateFrequency),
            SpatialResolution = own.Select(metadata => metadata.SpatialResolution).FirstOrDefault(value => value is not null),
            TemporalResolution = First(own, metadata => metadata.TemporalResolution),
            Links = governance.Links,
            MetadataDate = item.UpdatedAt ?? item.CreatedAt ?? graph.GeneratedAt,
        };
    }

    private static MetadataV2ObjectMetadata Fold(MetadataV2ObjectMetadata[] chain)
        => chain.Skip(1).Aggregate(chain[0], (effective, parent) => effective.WithServiceGovernanceFallbacks(parent));

    private static MetadataV2ObjectMetadata[] FolderChain(MetadataV2Graph graph, MetadataV2Service service)
    {
        var folder = graph.Folders.FirstOrDefault(candidate =>
            !candidate.Deleted && string.Equals(candidate.Metadata.Id, service.FolderId, StringComparison.Ordinal));
        return folder is null ? [graph.Metadata] : [folder.Metadata, graph.Metadata];
    }

    private static MetadataV2ResponsibleParty[] ContactsOf(MetadataV2ObjectMetadata metadata)
    {
        if (metadata.Contacts.Count > 0)
        {
            return metadata.Contacts.OfType<MetadataV2ResponsibleParty>().ToArray();
        }
        return metadata.ContactPoint is { } point && (Text(point.Name) ?? Text(point.Email) ?? Text(point.Url)) is not null
            ? [new MetadataV2ResponsibleParty { Name = Text(point.Name), Email = Text(point.Email), Url = Text(point.Url) }]
            : [];
    }

    private static string[] SupportedCrs(MetadataV2Resource resource)
    {
        var spatial = resource.Spatial;
        IEnumerable<MetadataV2SpatialReference?> references = spatial is null
            ? []
            : [spatial.SpatialReference, .. spatial.SupportedCrs];
        return references
            .Select(reference => reference?.ResolveSrid() is { } srid
                ? "EPSG:" + srid.ToString(CultureInfo.InvariantCulture)
                : Text(reference?.Crs))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Keywords and themes are trimmed and de-duplicated case-insensitively, keeping the first spelling.
    public static IReadOnlyList<string> NormalizedTerms(IReadOnlyList<string> terms)
        => terms.Select(Text).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyList<string> Terms(MetadataV2ObjectMetadata[] own, Func<MetadataV2ObjectMetadata, IReadOnlyList<string>> field)
        => own.Select(metadata => NormalizedTerms(field(metadata))).FirstOrDefault(values => values.Count > 0) ?? [];

    private static string? First(MetadataV2ObjectMetadata[] own, Func<MetadataV2ObjectMetadata, string?> field)
        => own.Select(field).Select(Text).FirstOrDefault(value => value is not null);

    private static IReadOnlyList<T> FirstList<T>(MetadataV2ObjectMetadata[] own, Func<MetadataV2ObjectMetadata, IReadOnlyList<T>> field)
        => own.Select(field).FirstOrDefault(values => values.Count > 0) ?? [];

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
