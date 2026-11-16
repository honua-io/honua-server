// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class NoOpVersionManager : IVersionManager
{
    public bool SupportsVersioning => false;

    public Task<GdbVersion> CreateAsync(CreateVersionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    public Task<bool> DeleteAsync(Guid versionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    public Task<GdbVersion?> AlterAsync(AlterVersionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    public Task<IReadOnlyList<GdbVersion>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GdbVersion>>(Array.Empty<GdbVersion>());

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

    public Task<VersionContext?> ResolveAsync(string? gdbVersion, string? callerOwner, CancellationToken cancellationToken = default)
        => ResolveAsync(gdbVersion, cancellationToken);

    public Task<VersionReconcileResult> ReconcileAsync(
        Guid versionId,
        VersionReconcilePolicy policy = VersionReconcilePolicy.None,
        VersionConflictDetection detection = VersionConflictDetection.ByAttribute,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    public Task<IReadOnlyList<VersionReconcileConflict>> GetPendingConflictsAsync(
        Guid versionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    public Task<VersionConflictResolutionResult> ResolveConflictsAsync(
        Guid versionId,
        IReadOnlyList<VersionConflictResolution> resolutions,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");

    public Task<VersionPostResult> PostAsync(Guid versionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Branch versioning is not supported by this data provider.");
}
