// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.DuckDB.Features.FeatureStore;

/// <summary>
/// Change tracker that reports no changes.
/// Registered when the DuckDB provider is active since DuckDB is read-only in V1.
/// </summary>
internal sealed class ReadOnlyChangeTracker : IChangeTracker
{
    /// <inheritdoc />
    public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0L);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<FeatureChange>>(Array.Empty<FeatureChange>());
    }
}
