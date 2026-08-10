// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Postgres.Features.Admin;

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

    internal static IReadOnlyDictionary<int, MetadataV2ObjectMetadata> IndexSourceGovernanceByStorageLayer(
        MetadataV2Graph graph,
        string serviceName)
    {
        var service = ResolveUniquePublishedFeatureService(graph, serviceName, layerId: null);
        if (service is null)
        {
            return new Dictionary<int, MetadataV2ObjectMetadata>();
        }

        var resourcesById = graph.Resources.ToDictionary(resource => resource.Metadata.Id, StringComparer.Ordinal);
        var bindingsById = graph.StorageBindings.ToDictionary(binding => binding.Metadata.Id, StringComparer.Ordinal);
        var metadataByLayerId = new Dictionary<int, MetadataV2ObjectMetadata>();
        foreach (var publication in graph.Publications.Where(publication =>
                     string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal)))
        {
            if (!resourcesById.TryGetValue(publication.ResourceId, out var resource))
            {
                continue;
            }

            var bindingId = publication.StorageBindingId ?? resource.PrimaryStorageBindingId;
            if (bindingId is not null &&
                bindingsById.TryGetValue(bindingId, out var binding) &&
                binding.StorageLayerId is { } layerId &&
                !metadataByLayerId.ContainsKey(layerId))
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
            LicenseUrl = FindManagedLink(metadata, "license"),
            SourceUrl = FindManagedLink(metadata, "describedby")
        };

    private static string? FindManagedLink(MetadataV2ObjectMetadata metadata, string relation)
        => metadata.Links.FirstOrDefault(link =>
            string.Equals(link.Rel, relation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.ManagedBy, LayerSourceGovernance.LinkManager, StringComparison.Ordinal))?.Href;
}
