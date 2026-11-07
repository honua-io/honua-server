// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2CollectionIds
{
    public static string? Advertised(MetadataV2Publication publication, MetadataV2Resource? resource)
    {
        var collectionId = publication.ServiceLocalId ?? publication.Path ?? resource?.Metadata.Name;
        return string.IsNullOrWhiteSpace(collectionId) ? resource?.Metadata.Id : collectionId;
    }

    public static Dictionary<string, string> ForMaps(MetadataV2GraphSnapshot snapshot)
    {
        var publications = snapshot.Index.PublicationsById.Values.Where(candidate =>
            snapshot.IsRoutable(candidate) &&
            snapshot.Index.ServicesById.TryGetValue(candidate.ServiceId, out var service) &&
            ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.OgcApiMaps)).ToArray();
        var aliases = new Dictionary<string, HashSet<(string ResourceId, string ServiceId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var publication in publications)
        {
            var resource = snapshot.ResolveResource(publication)!;
            foreach (var alias in new[] { publication.ServiceLocalId, publication.Path,
                         publication.Metadata.Name, publication.Metadata.Title, publication.Metadata.Id,
                         resource.Metadata.Name, resource.Metadata.Title, resource.Metadata.Id })
            {
                if (alias is null)
                {
                    continue;
                }

                if (!aliases.TryGetValue(alias, out var resources))
                {
                    resources = new HashSet<(string ResourceId, string ServiceId)>();
                    aliases.Add(alias, resources);
                }

                resources.Add((resource.Metadata.Id, publication.ServiceId));
            }
        }

        var publicationIds = snapshot.Graph.Publications.Select(publication => publication.Metadata.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return publications.ToDictionary(publication => publication.Metadata.Id, publication =>
        {
            var resource = snapshot.ResolveResource(publication)!;
            var alias = publication.ServiceLocalId ?? publication.Path ?? resource.Metadata.Name;
            var shadowsPublicationId = publicationIds.Contains(alias) &&
                !string.Equals(alias, publication.Metadata.Id, StringComparison.OrdinalIgnoreCase);
            return shadowsPublicationId || aliases[alias].Count > 1 ? publication.Metadata.Id : alias;
        }, StringComparer.Ordinal);
    }

}
