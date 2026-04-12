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
