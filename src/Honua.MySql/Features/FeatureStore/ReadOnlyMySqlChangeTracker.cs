// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.MySql.Features.FeatureStore;

/// <summary>
/// Change tracker that reports no changes.
/// Registered when the MySQL/MariaDB provider is active since the slice is read/query-only.
/// </summary>
internal sealed class ReadOnlyMySqlChangeTracker : IChangeTracker
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
