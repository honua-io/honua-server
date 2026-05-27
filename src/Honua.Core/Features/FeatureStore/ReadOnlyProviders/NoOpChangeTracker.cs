// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Change tracker that reports no changes. Registered by read-only feature providers
/// (DuckDB, MySQL/MariaDB) so DI activation succeeds for protocol handlers that depend
/// on <see cref="IChangeTracker"/> while the underlying slice is read/query-only.
/// </summary>
public sealed class NoOpChangeTracker : IChangeTracker
{
    /// <inheritdoc />
    public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FeatureChange>>(Array.Empty<FeatureChange>());
}
