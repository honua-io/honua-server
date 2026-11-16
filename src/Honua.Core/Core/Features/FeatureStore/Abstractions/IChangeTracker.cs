// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IChangeTracker
{
    async Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration, int[] layerIds, IReadOnlySet<long>? objectIds,
        string excludeOriginReplicaId, CancellationToken cancellationToken = default)
    {
        var changes = await GetChangesSinceAsync(sinceGeneration, layerIds, objectIds, cancellationToken).ConfigureAwait(false);
        return changes.Where(change => !string.Equals(change.OriginReplicaId, excludeOriginReplicaId, StringComparison.Ordinal)).ToList();
    }

    Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default);

    // True when any committed change, including one that later nets out, touched the layer after
    // the generation. The default counts a change to any layer, so it can only over-report.
    async Task<bool> HasLayerChangedSinceAsync(long sinceGeneration, int layerId, CancellationToken cancellationToken = default)
        => await GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false) > sinceGeneration;

    Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        IReadOnlySet<long>? objectIds,
        CancellationToken cancellationToken = default)
    {
        if (objectIds is null)
        {
            return await GetChangesSinceAsync(sinceGeneration, layerIds, cancellationToken).ConfigureAwait(false);
        }

        if (objectIds.Count == 0)
        {
            return [];
        }

        var changes = await GetChangesSinceAsync(sinceGeneration, layerIds, cancellationToken).ConfigureAwait(false);
        return changes
            .Where(change => objectIds.Contains(change.ObjectId)
                || change.PublicObjectId is { } publicObjectId && objectIds.Contains(publicObjectId))
            .ToList();
    }

    async Task<IReadOnlyList<FeatureChange>> GetChangesInWindowAsync(
        long sinceGeneration,
        long throughGeneration,
        int[] layerIds,
        string? excludeOriginReplicaId,
        CancellationToken cancellationToken = default)
    {
        var changes = excludeOriginReplicaId is null
            ? await GetChangesSinceAsync(sinceGeneration, layerIds, cancellationToken).ConfigureAwait(false)
            : await GetChangesSinceAsync(sinceGeneration, layerIds, objectIds: null, excludeOriginReplicaId, cancellationToken).ConfigureAwait(false);
        if (changes.All(change => change.Generation <= throughGeneration))
        {
            return changes;
        }

        throw new NotSupportedException(
            "This change tracker cannot collapse a bounded generation window; override GetChangesInWindowAsync.");
    }
}
