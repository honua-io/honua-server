// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IVersionManager
{
    bool SupportsVersioning { get; }

    Task<GdbVersion> CreateAsync(CreateVersionRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid versionId, CancellationToken cancellationToken = default);

    Task<GdbVersion?> AlterAsync(AlterVersionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GdbVersion>> ListAsync(CancellationToken cancellationToken = default);

    Task<VersionContext?> ResolveAsync(string? gdbVersion, CancellationToken cancellationToken = default);

    Task<VersionContext?> ResolveAsync(string? gdbVersion, string? callerOwner, CancellationToken cancellationToken = default);

    Task<VersionReconcileResult> ReconcileAsync(
        Guid versionId,
        VersionReconcilePolicy policy = VersionReconcilePolicy.None,
        VersionConflictDetection detection = VersionConflictDetection.ByAttribute,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VersionReconcileConflict>> GetPendingConflictsAsync(
        Guid versionId,
        CancellationToken cancellationToken = default);

    Task<VersionConflictResolutionResult> ResolveConflictsAsync(
        Guid versionId,
        IReadOnlyList<VersionConflictResolution> resolutions,
        CancellationToken cancellationToken = default);

    Task<VersionPostResult> PostAsync(Guid versionId, CancellationToken cancellationToken = default);
}
