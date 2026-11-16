// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IWorkspaceLifecycleService
{
    Task<Workspace?> GetWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);

    Task<Workspace> CreateWorkspaceAsync(
        WorkspaceKind kind,
        string label,
        string ownerId,
        string? scopeId = null,
        TimeSpan? customTtl = null,
        CancellationToken cancellationToken = default);

    Task<Artifact> AddArtifactAsync(
        string workspaceId,
        ArtifactKind kind,
        string label,
        string? uri = null,
        string? contentType = null,
        long sizeBytes = 0,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<Artifact> AddOrReplaceArtifactAsync(
        string workspaceId,
        ArtifactKind kind,
        string label,
        bool overwrite,
        string? uri = null,
        string? contentType = null,
        long sizeBytes = 0,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<Artifact?> PublishArtifactAsync(WorkspaceArtifactPublication publication,
        Func<CancellationToken, Task<bool>> publishReference, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Atomic workspace publication is unavailable.");

    Task<Workspace> GetOrCreateNamedWorkspaceAsync(
        string ownerId,
        string label,
        CancellationToken cancellationToken = default);

    Task<Workspace> GetOrCreateScopedWorkspaceAsync(
        string ownerId,
        string label,
        string? scopeId,
        CancellationToken cancellationToken = default)
        => scopeId is null
            ? GetOrCreateNamedWorkspaceAsync(ownerId, label, cancellationToken)
            : throw new NotSupportedException("Scoped workspace storage is unavailable.");

    Task<ArtifactPromotionResult> PromoteArtifactAsync(
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ExtendWorkspaceExpirationAsync(
        string workspaceId,
        TimeSpan extension,
        CancellationToken cancellationToken = default);

    Task<CleanupResult> RunCleanupAsync(CancellationToken cancellationToken = default);
}
