// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Adapts the scoped <see cref="IChangeTracker"/> into the singleton-friendly
/// <see cref="IChangeFeedGenerationProbe"/> the event-trigger background service consumes.
/// Resolves a fresh <see cref="IChangeTracker"/> per probe from a request-style scope so the
/// background loop never captures a scoped service on the root provider.
/// </summary>
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

        var layerIds = watchedLayerIds as int[] ?? watchedLayerIds.ToArray();
        var changes = await tracker
            .GetChangesSinceAsync(sinceGeneration, layerIds, cancellationToken)
            .ConfigureAwait(false);

        if (changes.Count == 0)
        {
            return null;
        }

        var maxGeneration = sinceGeneration;
        foreach (var change in changes)
        {
            if (change.Generation > maxGeneration)
            {
                maxGeneration = change.Generation;
            }
        }

        return maxGeneration > sinceGeneration ? maxGeneration : null;
    }
}
