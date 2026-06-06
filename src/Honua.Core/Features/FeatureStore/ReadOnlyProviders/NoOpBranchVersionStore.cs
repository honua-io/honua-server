// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Branch-version store for read-only feature providers (DuckDB, MySQL/MariaDB). Branch
/// versioning requires write workflows, so no named versions can exist; DEFAULT always
/// resolves to the base storage layer id and named-version creation is rejected. Registered
/// so DI activation succeeds for protocol handlers that depend on
/// <see cref="IBranchVersionStore"/> while the underlying slice is read/query-only.
/// </summary>
public sealed class NoOpBranchVersionStore : IBranchVersionStore
{
    /// <inheritdoc />
    public Task<BranchVersion> CreateVersionAsync(
        string serviceId,
        string versionName,
        int baseLayerId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch-versioned editing is not supported by read-only feature providers.");

    /// <inheritdoc />
    public Task<BranchVersion?> GetVersionAsync(
        string serviceId,
        string versionName,
        CancellationToken cancellationToken = default)
        => Task.FromResult<BranchVersion?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<BranchVersion>> ListVersionsAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BranchVersion>>([]);

    /// <inheritdoc />
    public Task<int?> ResolveBranchLayerIdAsync(
        string serviceId,
        string? versionName,
        int baseLayerId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(
            IBranchVersionStore.IsDefaultVersion(versionName) ? baseLayerId : null);
}
