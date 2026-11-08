// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal sealed record DiscoveryMetadataTarget(
    MetadataV2ObjectMetadata Metadata,
    IReadOnlySet<string> ResourceIds,
    IReadOnlySet<string> ServiceIds,
    IReadOnlyList<DiscoveryPublicationImpact> Publications)
{
    public static DiscoveryMetadataTarget? ForLayer(MetadataV2GraphSnapshot snapshot, int storageLayerId)
    {
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(storageLayerId, out var resource))
            return null;
        var resourceIds = new HashSet<string>(StringComparer.Ordinal) { resource.Metadata.Id };
        return new(resource.Metadata, resourceIds, new HashSet<string>(), Impact(snapshot,
            snapshot.Graph.Publications.Where(publication => resourceIds.Contains(publication.ResourceId))));
    }

    public static DiscoveryMetadataTarget? ForService(MetadataV2GraphSnapshot snapshot, string serviceName)
    {
        var services = snapshot.Graph.Services.Where(service =>
            string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (services.Length == 0)
            return null;
        var serviceIds = services.Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        return new(services[0].Metadata, new HashSet<string>(), serviceIds, Impact(snapshot,
            snapshot.Graph.Publications.Where(publication => serviceIds.Contains(publication.ServiceId))));
    }

    public MetadataV2Graph Apply(MetadataV2Graph graph, DiscoveryMetadataUpdateRequest request) => graph with
    {
        Resources = graph.Resources.Select(resource => ResourceIds.Contains(resource.Metadata.Id)
            ? resource with { Metadata = ApplyFields(resource.Metadata, request) } : resource).ToArray(),
        Services = graph.Services.Select(service => ServiceIds.Contains(service.Metadata.Id)
            ? service with { Metadata = ApplyFields(service.Metadata, request) } : service).ToArray(),
        Revision = graph.Revision + 1
    };

    private static MetadataV2ObjectMetadata ApplyFields(MetadataV2ObjectMetadata metadata,
        DiscoveryMetadataUpdateRequest request) => metadata with
        {
            Title = request.Title,
            Description = request.Description,
            Keywords = request.Keywords
        };

    private static DiscoveryPublicationImpact[] Impact(MetadataV2GraphSnapshot snapshot,
        IEnumerable<MetadataV2Publication> publications) => publications.Select(publication => new DiscoveryPublicationImpact(
            publication.Metadata.Id, snapshot.Index.ServicesById[publication.ServiceId].Metadata.Name,
            ReadPublicationIdentifier(publication))).ToArray();

    private static string ReadPublicationIdentifier(MetadataV2Publication publication)
    {
        var publicationWithIdentifier = publication.Identifier is null ? publication with { Identifier = new() } : publication;
        return publicationWithIdentifier.ServiceLocalId ?? publicationWithIdentifier.LayerIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
