// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Version manager for providers that do not support branch versioning (DuckDB, MySQL/MariaDB, and any
/// non-Postgres feature slice). Reports <see cref="SupportsVersioning"/> = false, resolves the DEFAULT
/// version (so non-versioned read/edit paths stay byte-identical) and rejects every other operation.
/// Mirrors the no-op change-tracker stub so DI activation succeeds for protocol handlers that depend on
/// <see cref="IVersionManager"/> while the underlying slice is read/query-only or non-Postgres (#1272).
/// </summary>
public sealed class NoOpVersionManager : IVersionManager
{
    /// <inheritdoc />
    public bool SupportsVersioning => false;

    /// <inheritdoc />
    public Task<GdbVersion> CreateAsync(CreateVersionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    /// <inheritdoc />
    public Task<bool> DeleteAsync(Guid versionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    /// <inheritdoc />
    public Task<GdbVersion?> AlterAsync(AlterVersionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    /// <inheritdoc />
    public Task<IReadOnlyList<GdbVersion>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GdbVersion>>(Array.Empty<GdbVersion>());

    /// <inheritdoc />
    public Task<VersionContext?> ResolveAsync(string? gdbVersion, CancellationToken cancellationToken = default)
    {
        // Absent/empty or the DEFAULT sentinel resolves to DEFAULT; any named version is unknown here.
        if (string.IsNullOrWhiteSpace(gdbVersion) ||
            gdbVersion.Trim().Equals("sde.default", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<VersionContext?>(VersionContext.Default);
        }

        return Task.FromResult<VersionContext?>(null);
    }

    /// <inheritdoc />
    public Task<VersionReconcileResult> ReconcileAsync(
        Guid versionId,
        VersionReconcilePolicy policy = VersionReconcilePolicy.None,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    /// <inheritdoc />
    public Task<IReadOnlyList<VersionReconcileConflict>> GetPendingConflictsAsync(
        Guid versionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    /// <inheritdoc />
    public Task<VersionConflictResolutionResult> ResolveConflictsAsync(
        Guid versionId,
        IReadOnlyList<VersionConflictResolution> resolutions,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    /// <inheritdoc />
    public Task<VersionPostResult> PostAsync(Guid versionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");
}
