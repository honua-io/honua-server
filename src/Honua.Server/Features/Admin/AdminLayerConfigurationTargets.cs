// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin;

// A layer index is service-local, so a write addresses one feature or table resource; a raster sidecar that shares the index is not a second target, and an unset identifier is not numeric.
internal static class AdminLayerConfigurationTargets
{
    private const int MetadataMutationMaxAttempts = 5;

    // The result is a conflict when more than one resource matches the layer id.
    internal readonly record struct AdminLayerConfigurationTarget(
        MetadataV2Object? Resource,
        string? ConflictDetail)
    {
        public bool IsAmbiguous => ConflictDetail is not null;

        public static AdminLayerConfigurationTarget Unique(MetadataV2Object resource) => new(resource, null);

        public static AdminLayerConfigurationTarget Ambiguous(string detail) => new(null, detail);
    }

    internal static AdminLayerConfigurationTarget Resolve(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var resources = new Dictionary<string, MetadataV2Object>(StringComparer.Ordinal);
        var serviceNames = new List<string>();
        var seenServices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex != layerId || !snapshot.IsRoutable(publication))
            {
                continue;
            }

            var candidate = snapshot.ResolveResource(publication);
            if (candidate?.Type is not (MetadataV2ObjectType.FeatureDataset or MetadataV2ObjectType.Table))
            {
                continue;
            }

            resources.TryAdd(candidate.Metadata.Id, candidate);
            if (seenServices.Add(publication.ServiceId))
            {
                serviceNames.Add(ServiceName(snapshot, publication));
            }
        }

        if (resources.Count == 1)
        {
            return AdminLayerConfigurationTarget.Unique(resources.Values.First());
        }

        if (resources.Count == 0)
        {
            return default;
        }

        var listed = serviceNames.Count == 0
            ? "multiple services"
            : string.Join(", ", serviceNames.Select(static name => $"'{name}'"));
        return AdminLayerConfigurationTarget.Ambiguous(
            $"Layer {layerId} is published by more than one resource ({listed}). Update one service's layer at a time.");
    }

    internal static async Task<AdminLayerConfigurationTarget> WriteAsync(
        IMetadataV2GraphStore graphStore,
        int layerId,
        Func<MetadataV2Object, MetadataV2Object> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(mutate);

        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var target = Resolve(snapshot, layerId);
            if (target.IsAmbiguous || target.Resource is null)
            {
                return target;
            }

            var resources = snapshot.Graph.Resources.ToArray();
            var mutated = false;
            for (var i = 0; i < resources.Length; i++)
            {
                if (!string.Equals(resources[i].Metadata.Id, target.Resource.Metadata.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                resources[i] = mutate(resources[i]);
                mutated = true;
            }

            if (!mutated)
            {
                return target;
            }

            var updated = snapshot.Graph with
            {
                Resources = resources,
                Revision = snapshot.Graph.Revision + 1,
            };

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return target;
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply.
            }
        }
    }

    private static string ServiceName(MetadataV2GraphSnapshot snapshot, MetadataV2Layer publication)
    {
        if (snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service)
            && !string.IsNullOrWhiteSpace(service.Metadata.Name))
        {
            return service.Metadata.Name;
        }

        return publication.ServiceId;
    }

    private static bool IsEtagMismatch(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.Contains("etag mismatch", StringComparison.OrdinalIgnoreCase);
}
