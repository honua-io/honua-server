// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Orchestration;

// Each probe opens a scope so the background loop never captures a scoped service.
internal sealed class ChangeTrackerGenerationProbe(IServiceScopeFactory scopeFactory) : IChangeFeedGenerationProbe
{
    public async Task<long?> GetLatestGenerationAsync(
        long sinceGeneration,
        IReadOnlyList<int> watchedLayerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watchedLayerIds);
        if (watchedLayerIds.Count == 0)
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var tracker = scope.ServiceProvider.GetService<IChangeTracker>();
        if (tracker is null)
        {
            // Read-only / non-Postgres providers register no change tracker; the change-feed
            // trigger is a no-op there rather than a hard failure.
            return null;
        }

        var currentGeneration = await tracker
            .GetCurrentGenerationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentGeneration <= sinceGeneration)
        {
            return null;
        }

        var layerIds = watchedLayerIds as int[] ?? watchedLayerIds.ToArray();
        var changes = await tracker
            .GetChangesInWindowAsync(
                sinceGeneration, currentGeneration, layerIds,
                excludeOriginReplicaId: null, cancellationToken)
            .ConfigureAwait(false);

        var maxGeneration = changes.Select(change => change.Generation).DefaultIfEmpty(sinceGeneration).Max();
        return maxGeneration > sinceGeneration ? maxGeneration : null;
    }
}
