// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private async Task<IReadOnlyList<PublishedLayerSummary>> HydrateSourceGovernanceAsync(
        List<PublishedLayerSummary> layers,
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (layers.Count == 0)
        {
            return layers;
        }

        var (graph, _) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        var metadataByLayerId = IndexSourceGovernanceByStorageLayer(graph, serviceName);
        return layers
            .Select(layer => metadataByLayerId.TryGetValue(layer.LayerId, out var metadata)
                ? HydrateSourceGovernance(layer, metadata)
                : layer)
            .ToArray();
    }

    internal static PublishedLayerSummary HydrateSourceGovernance(
        PublishedLayerSummary layer,
        MetadataV2Graph graph,
        string serviceName)
    {
        var metadataByLayerId = IndexSourceGovernanceByStorageLayer(graph, serviceName);
        return metadataByLayerId.TryGetValue(layer.LayerId, out var metadata)
            ? HydrateSourceGovernance(layer, metadata)
            : layer;
    }

    internal static IReadOnlyDictionary<int, MetadataV2ObjectMetadata> IndexSourceGovernanceByStorageLayer(
        MetadataV2Graph graph,
        string serviceName)
    {
        var service = ResolveUniquePublishedFeatureService(graph, serviceName, layerId: null);
        if (service is null)
        {
            return new Dictionary<int, MetadataV2ObjectMetadata>();
        }

        var snapshot = new MetadataV2GraphSnapshot(graph, "\"source-governance\"", DateTimeOffset.UtcNow);
        var metadataByLayerId = new Dictionary<int, MetadataV2ObjectMetadata>();
        foreach (var publication in graph.Publications.Where(publication =>
                     string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal) &&
                     publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer)
                 .OrderByDescending(snapshot.IsRoutable)
                 .ThenByDescending(publication =>
                     publication.Status.Lifecycle != MetadataV2LifecycleStatus.Retired &&
                     snapshot.Index.ResourcesById.TryGetValue(publication.ResourceId, out var resource) &&
                     resource.Status.Lifecycle != MetadataV2LifecycleStatus.Retired)
                 .ThenByDescending(publication =>
                     publication.Status.Lifecycle == MetadataV2LifecycleStatus.Active &&
                     snapshot.Index.ResourcesById.TryGetValue(publication.ResourceId, out var resource) &&
                     resource.Status.Lifecycle == MetadataV2LifecycleStatus.Active))
        {
            var resource = snapshot.ResolveResource(publication);
            var binding = snapshot.ResolveStorageBinding(publication);
            if (resource is null || binding?.StorageLayerId is not { } layerId)
            {
                continue;
            }

            if (!metadataByLayerId.ContainsKey(layerId))
            {
                metadataByLayerId.Add(layerId, resource.Metadata);
            }
        }

        return metadataByLayerId;
    }

    private async Task<PublishedLayerSummary?> HydrateSourceGovernanceAsync(
        PublishedLayerSummary? layer,
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (layer is null)
        {
            return null;
        }

        var hydrated = await HydrateSourceGovernanceAsync([layer], serviceName, cancellationToken).ConfigureAwait(false);
        return hydrated[0];
    }

    private static PublishedLayerSummary HydrateSourceGovernance(
        PublishedLayerSummary layer,
        MetadataV2ObjectMetadata metadata)
        => new()
        {
            LayerId = layer.LayerId,
            LayerName = layer.LayerName,
            Description = layer.Description,
            Schema = layer.Schema,
            Table = layer.Table,
            GeometryType = layer.GeometryType,
            Srid = layer.Srid,
            PrimaryKey = layer.PrimaryKey,
            FieldCount = layer.FieldCount,
            Enabled = layer.Enabled,
            ServiceName = layer.ServiceName,
            License = metadata.License,
            Attribution = metadata.Attribution,
            Publisher = metadata.Publisher,
            LicenseUrl = FindCanonicalGovernanceLink(metadata, "license"),
            SourceUrl = FindCanonicalGovernanceLink(metadata, "describedby")
        };

    internal static string? FindCanonicalGovernanceLink(MetadataV2ObjectMetadata metadata, string relation)
    {
        var relationLinks = metadata.Links
            .Where(link => string.Equals(link.Rel, relation, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return relationLinks.FirstOrDefault(link =>
                   string.Equals(
                       link.ManagedBy,
                       LayerSourceGovernance.LinkManager,
                       StringComparison.Ordinal))?.Href ??
               relationLinks.FirstOrDefault()?.Href;
    }
}
