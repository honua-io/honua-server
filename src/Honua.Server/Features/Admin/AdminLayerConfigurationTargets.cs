// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Resolves the single feature or table resource addressed by an admin layer id.
/// </summary>
/// <remarks>
/// Layer indexes are service-local. Filter, field, popup, and drawing writes must
/// not fan out across every resource that happens to reuse the index. A raster
/// sidecar may share the index with its feature layer and is not a second target.
/// Matching uses <see cref="MetadataV2Publication.LayerIndex"/> alone: legacy
/// snapshots leave <see cref="MetadataV2Publication.Identifier"/> unset, and
/// reading <c>Identifier.IsNumeric</c> throws.
/// </remarks>
internal static class AdminLayerConfigurationTargets
{
    private const int MetadataMutationMaxAttempts = 5;

    /// <summary>The resource a layer-id write may update, or a conflict when the id is shared.</summary>
    /// <param name="Resource">The single routable feature or table resource, when the id is unique.</param>
    /// <param name="ConflictDetail">Why the id cannot be written, when more than one resource matches.</param>
    internal readonly record struct AdminLayerConfigurationTarget(
        MetadataV2Resource? Resource,
        string? ConflictDetail)
    {
        /// <summary>True when more than one routable feature or table resource publishes the layer id.</summary>
        public bool IsAmbiguous => ConflictDetail is not null;

        /// <summary>Creates a unique target.</summary>
        public static AdminLayerConfigurationTarget Unique(MetadataV2Resource resource) => new(resource, null);

        /// <summary>Creates an ambiguous-id conflict.</summary>
        public static AdminLayerConfigurationTarget Ambiguous(string detail) => new(null, detail);
    }

    /// <summary>
    /// Resolves <paramref name="layerId"/> to one routable feature dataset or table.
    /// </summary>
    internal static AdminLayerConfigurationTarget Resolve(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var resources = new Dictionary<string, MetadataV2Resource>(StringComparer.Ordinal);
        var serviceNames = new List<string>();
        var seenServices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex != layerId || !snapshot.IsRoutable(publication))
            {
                continue;
            }

            var candidate = snapshot.ResolveResource(publication);
            if (candidate?.Type is not (MetadataV2ResourceType.FeatureDataset or MetadataV2ResourceType.Table))
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

    /// <summary>
    /// Applies <paramref name="mutate"/> to the single resource for <paramref name="layerId"/>.
    /// An ambiguous id is not written. Retries an optimistic-concurrency etag mismatch.
    /// </summary>
    internal static async Task<AdminLayerConfigurationTarget> WriteAsync(
        IMetadataV2GraphStore graphStore,
        int layerId,
        Func<MetadataV2Resource, MetadataV2Resource> mutate,
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

    private static string ServiceName(MetadataV2GraphSnapshot snapshot, MetadataV2Publication publication)
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
