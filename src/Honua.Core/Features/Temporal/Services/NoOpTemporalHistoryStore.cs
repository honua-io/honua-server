// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Services;

/// <summary>
/// Temporal history store that reports no history (honua-server#1166). Registered as the universal
/// fallback so DI activation succeeds for every provider; the Postgres provider overrides it with the
/// change-log reader. Read-only providers (DuckDB, MySQL/MariaDB) keep this no-op store, matching the
/// no-op change tracker they register, so their layers report history as unsupported.
/// </summary>
public sealed class NoOpTemporalHistoryStore : ITemporalHistoryStore
{
    private static readonly IReadOnlyList<TemporalChangeRecord> Empty = Array.Empty<TemporalChangeRecord>();

    /// <inheritdoc />
    public Task<IReadOnlyList<TemporalChangeRecord>> GetFeatureRevisionsAsync(
        int storageLayerId,
        long objectId,
        long afterGeneration,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Empty);

    /// <inheritdoc />
    public Task<IReadOnlyList<TemporalChangeRecord>> GetChangesInWindowAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        long afterObjectId,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Empty);

    /// <inheritdoc />
    public Task<int> CountChangedFeaturesAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <inheritdoc />
    public Task<long?> ResolveCheckpointGenerationAsync(
        int storageLayerId,
        TemporalCheckpointKind kind,
        string value,
        long upperBoundGeneration,
        CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(null);
}
