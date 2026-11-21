// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    internal sealed class ServiceLayerIndexAllocationRequest
    {
        public required string ServiceId { get; init; }

        public required string ServiceName { get; init; }

        public int? RequestedIndex { get; init; }

        public required string ResourceId { get; init; }

        public required string BindingId { get; init; }
    }

    internal static int ResolvePublishedLayerIndex(
        MetadataV2Graph graph,
        (string ServiceId, string ServiceName, int? RequestedIndex) target,
        (string ResourceId, string BindingId) source)
        => ResolveServiceLayerIndexForPublish(
            graph,
            new ServiceLayerIndexAllocationRequest
            {
                ServiceId = target.ServiceId,
                ServiceName = target.ServiceName,
                RequestedIndex = target.RequestedIndex,
                ResourceId = source.ResourceId,
                BindingId = source.BindingId
            });

    internal static int ResolveServiceLayerIndexForPublish(
        MetadataV2Graph graph,
        ServiceLayerIndexAllocationRequest allocation)
        => allocation.RequestedIndex is { } requested
            ? ValidateRequestedServiceLayerIndex(graph, allocation, requested)
            : AllocateNextFreeServiceLayerIndex(graph, allocation.ServiceId, allocation.ServiceName);

    private static int ValidateRequestedServiceLayerIndex(
        MetadataV2Graph graph,
        ServiceLayerIndexAllocationRequest allocation,
        int requested)
    {
        if (requested < 0)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "serviceLayerIndex must be zero or greater.");
        }

        var colliding = graph.Publications.FirstOrDefault(publication =>
            string.Equals(publication.ServiceId, allocation.ServiceId, StringComparison.Ordinal) &&
            publication.LayerIndex == requested &&
            (!string.Equals(publication.ResourceId, allocation.ResourceId, StringComparison.Ordinal) ||
             !string.Equals(publication.StorageBindingId, allocation.BindingId, StringComparison.Ordinal)));
        if (colliding is not null)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                $"Service '{allocation.ServiceName}' already publishes FeatureServer layer {requested} from another storage binding.");
        }

        return requested;
    }

    private static int AllocateNextFreeServiceLayerIndex(
        MetadataV2Graph graph,
        string serviceId,
        string serviceName)
    {
        var existingIndices = graph.Publications
            .Where(publication =>
                string.Equals(publication.ServiceId, serviceId, StringComparison.Ordinal) &&
                publication.LayerIndex is >= 0)
            .Select(static publication => publication.LayerIndex!.Value)
            .ToArray();
        if (existingIndices.Length == 0)
        {
            return 0;
        }

        var maxIndex = existingIndices.Max();
        if (maxIndex < int.MaxValue)
        {
            return maxIndex + 1;
        }

        var used = existingIndices.ToHashSet();
        for (var candidate = 0; candidate < int.MaxValue; candidate++)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new LayerPublishingException(
            LayerPublishingErrorKind.Conflict,
            $"Service '{serviceName}' has no free layer index available.");
    }

    private static int? FindLinkedServiceLayerIndex(
        MetadataV2Graph graph,
        string serviceName,
        int storageLayerId)
    {
        var linkedService = ResolveUniquePublishedFeatureService(graph, serviceName, storageLayerId);
        if (linkedService is null)
        {
            return null;
        }

        return graph.Publications.FirstOrDefault(publication =>
            string.Equals(publication.ServiceId, linkedService.Metadata.Id, StringComparison.Ordinal) &&
            publication.PublicationType == MetadataV2LayerType.EsriFeatureLayer &&
            graph.StorageBindings.Any(binding =>
                string.Equals(binding.Metadata.Id, publication.StorageBindingId, StringComparison.Ordinal) &&
                binding.StorageLayerId == storageLayerId))?.LayerIndex;
    }
}
