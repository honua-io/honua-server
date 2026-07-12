// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Orchestrates workspace and artifact lifecycle operations including creation,
/// expiration, cleanup, and promotion.
/// </summary>
public interface IWorkspaceLifecycleService
{
    /// <summary>
    /// Retrieves a workspace by identifier, or <c>null</c> when the workspace
    /// does not exist in the lifecycle store.
    /// </summary>
    Task<Workspace?> GetWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a workspace with retention policy applied.
    /// </summary>
    Task<Workspace> CreateWorkspaceAsync(
        WorkspaceKind kind,
        string label,
        string ownerId,
        string? scopeId = null,
        TimeSpan? customTtl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an artifact to an existing, active workspace.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The workspace does not exist or is not in the <see cref="WorkspaceLifecycleState.Active"/> state.
    /// </exception>
    Task<Artifact> AddArtifactAsync(
        string workspaceId,
        ArtifactKind kind,
        string label,
        string? uri = null,
        string? contentType = null,
        long sizeBytes = 0,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an artifact to an existing, active workspace, honoring overwrite
    /// semantics for a caller-supplied stable output label (the same label a
    /// re-run of the same tool would reuse). Mirrors arcpy's
    /// <c>arcpy.env.overwriteOutput</c> behavior for a workspace-scoped output:
    /// when an <see cref="ArtifactLifecycleState.Available"/> artifact with the
    /// same label already exists in the workspace, <c>overwrite = false</c>
    /// rejects the write instead of silently clobbering the existing output,
    /// while <c>overwrite = true</c> replaces it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The workspace does not exist or is not in the <see cref="WorkspaceLifecycleState.Active"/> state.
    /// </exception>
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

    /// <summary>
    /// Resolves the caller's workspace for a stable label, creating one when
    /// none exists yet. Used to map a caller-supplied identifier (e.g. GPServer's
    /// <c>env:workspace</c>) onto a durable <see cref="Workspace"/> without
    /// requiring a separate workspace-provisioning call. Matches on the most
    /// recently created <see cref="WorkspaceLifecycleState.Active"/> workspace
    /// owned by <paramref name="ownerId"/> with the given <paramref name="label"/>;
    /// creates a new <see cref="WorkspaceKind.Scratch"/> workspace when no match
    /// is found.
    /// </summary>
    Task<Workspace> GetOrCreateNamedWorkspaceAsync(
        string ownerId,
        string label,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes an artifact from a temporary workspace to a durable destination.
    /// </summary>
    Task<ArtifactPromotionResult> PromoteArtifactAsync(
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the expiration of a workspace, subject to policy limits.
    /// </summary>
    Task<bool> ExtendWorkspaceExpirationAsync(
        string workspaceId,
        TimeSpan extension,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a cleanup sweep: expires overdue workspaces and deletes those
    /// already in expired state beyond the grace period.
    /// </summary>
    Task<CleanupResult> RunCleanupAsync(CancellationToken cancellationToken = default);
}
