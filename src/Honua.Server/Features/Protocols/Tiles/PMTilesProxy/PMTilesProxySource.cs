// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

/// <summary>
/// The publication a PMTiles archive was built from, re-resolved against the
/// current metadata graph so a retired or rebound source cannot be read.
/// </summary>
internal sealed record PMTilesPublishedSource(
    MetadataV2Resource Resource,
    MetadataV2Service Service,
    MetadataV2Publication Publication);

/// <summary>
/// Binds a published PMTiles object to the service and layer it was generated
/// from. Object metadata written at publish time is authoritative; artifacts
/// that predate those keys fall back to the layer id alone.
/// </summary>
internal static class PMTilesProxySourceResolver
{
    internal const string LayerIdMetadataKey = "layerId";
    internal const string ServiceIdMetadataKey = "serviceId";
    internal const string PublicationIdMetadataKey = "publicationId";
    internal const string ResourceIdMetadataKey = "resourceId";

    internal static PMTilesPublishedSource? ResolveForPublish(
        MetadataV2GraphSnapshot snapshot,
        string? serviceName,
        int layerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.IsNullOrWhiteSpace(serviceName) &&
            snapshot.FindService(serviceName) is { } named &&
            named.IsRoutable())
        {
            var publication = FindLayerPublication(snapshot, named.Metadata.Id, layerId);
            if (publication is not null &&
                snapshot.ResolveResource(publication) is { } resource)
            {
                return new PMTilesPublishedSource(resource, named, publication);
            }
        }

        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resourceByStorage))
        {
            return null;
        }

        foreach (var publication in snapshot.Index.PublicationsByResource[resourceByStorage.Metadata.Id])
        {
            if (!snapshot.IsRoutable(publication) ||
                (publication.LayerIndex != layerId &&
                 snapshot.ResolveStorageLayerId(publication) != layerId))
            {
                continue;
            }

            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
                !service.IsRoutable())
            {
                continue;
            }

            return new PMTilesPublishedSource(resourceByStorage, service, publication);
        }

        return null;
    }

    internal static PMTilesPublishedSource? Resolve(
        MetadataV2GraphSnapshot snapshot,
        IEnumerable<KeyValuePair<string, string>> metadata)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metadata);

        if (!TryReadMetadata(metadata, out var values) ||
            !values.TryGetValue(LayerIdMetadataKey, out var layerText) ||
            !int.TryParse(layerText, NumberStyles.None, CultureInfo.InvariantCulture, out var layerId))
        {
            return null;
        }

        if (values.TryGetValue(PublicationIdMetadataKey, out var publicationId) &&
            !string.IsNullOrWhiteSpace(publicationId))
        {
            return ResolveStamped(snapshot, values, layerId, publicationId);
        }

        // Archives published before the stamp exist. They may name a service, or
        // only a layer. Either way an ambiguous or cross-service bind is not
        // served: the first routable publication of a shared storage layer can
        // be a different, public service.
        values.TryGetValue(ServiceIdMetadataKey, out var serviceKey);
        return ResolveLegacy(snapshot, serviceKey, layerId);
    }

    /// <summary>
    /// Binds an unstamped archive. A service key limits the candidates to that
    /// service id or name. With no key, the storage layer must belong to exactly
    /// one routable service. Layer index alone never selects a publication whose
    /// storage handle is a different layer.
    /// </summary>
    private static PMTilesPublishedSource? ResolveLegacy(
        MetadataV2GraphSnapshot snapshot,
        string? serviceKey,
        int layerId)
    {
        var matches = new List<PMTilesPublishedSource>();
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (!snapshot.IsRoutable(publication) ||
                snapshot.ResolveStorageLayerId(publication) != layerId)
            {
                continue;
            }

            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
                !service.IsRoutable())
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(serviceKey) &&
                !string.Equals(serviceKey, service.Metadata.Id, StringComparison.Ordinal) &&
                !string.Equals(serviceKey, service.Metadata.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            matches.Add(new PMTilesPublishedSource(resource, service, publication));
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var serviceId = matches[0].Service.Metadata.Id;
        var resourceId = matches[0].Resource.Metadata.Id;
        for (var i = 1; i < matches.Count; i++)
        {
            if (!string.Equals(matches[i].Service.Metadata.Id, serviceId, StringComparison.Ordinal) ||
                !string.Equals(matches[i].Resource.Metadata.Id, resourceId, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return matches[0];
    }

    internal static ImmutableDictionary<string, string> StampPublishMetadata(
        ImmutableDictionary<string, string> metadata,
        PMTilesPublishedSource? source)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (source is null)
        {
            return metadata;
        }

        return metadata
            .Add(ServiceIdMetadataKey, source.Service.Metadata.Id)
            .Add(PublicationIdMetadataKey, source.Publication.Metadata.Id)
            .Add(ResourceIdMetadataKey, source.Resource.Metadata.Id);
    }

    private static PMTilesPublishedSource? ResolveStamped(
        MetadataV2GraphSnapshot snapshot,
        Dictionary<string, string> values,
        int layerId,
        string publicationId)
    {
        if (!snapshot.Index.PublicationsById.TryGetValue(publicationId, out var publication) ||
            !snapshot.IsRoutable(publication))
        {
            return null;
        }

        if (publication.LayerIndex != layerId &&
            snapshot.ResolveStorageLayerId(publication) != layerId)
        {
            return null;
        }

        var resource = snapshot.ResolveResource(publication);
        if (resource is null)
        {
            return null;
        }

        if (values.TryGetValue(ResourceIdMetadataKey, out var resourceId) &&
            !string.IsNullOrWhiteSpace(resourceId) &&
            !string.Equals(resourceId, resource.Metadata.Id, StringComparison.Ordinal))
        {
            return null;
        }

        if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
            !service.IsRoutable())
        {
            return null;
        }

        if (values.TryGetValue(ServiceIdMetadataKey, out var serviceId) &&
            !string.IsNullOrWhiteSpace(serviceId) &&
            !string.Equals(serviceId, service.Metadata.Id, StringComparison.Ordinal) &&
            !string.Equals(serviceId, service.Metadata.Name, StringComparison.Ordinal))
        {
            return null;
        }

        return new PMTilesPublishedSource(resource, service, publication);
    }

    private static MetadataV2Publication? FindLayerPublication(
        MetadataV2GraphSnapshot snapshot,
        string serviceId,
        int layerId)
    {
        var byIndex = snapshot.FindPublicationByLayerIndex(serviceId, layerId);
        if (byIndex is not null)
        {
            return byIndex;
        }

        foreach (var publication in snapshot.Index.PublicationsByService[serviceId])
        {
            if (snapshot.IsRoutable(publication) &&
                snapshot.ResolveStorageLayerId(publication) == layerId)
            {
                return publication;
            }
        }

        return null;
    }

    private static bool TryReadMetadata(
        IEnumerable<KeyValuePair<string, string>> metadata,
        out Dictionary<string, string> values)
    {
        // S3 lowercases user-metadata keys. Fail closed when a provider returns
        // two spellings of the same key rather than picking one.
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata)
        {
            if (!values.TryAdd(pair.Key, pair.Value))
            {
                return false;
            }
        }

        return true;
    }
}
