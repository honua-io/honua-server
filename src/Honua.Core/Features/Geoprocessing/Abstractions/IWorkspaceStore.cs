// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Storage operations for workspace lifecycle management.
/// </summary>
public interface IWorkspaceStore
{
    /// <summary>
    /// Creates a new workspace and returns its assigned identifier.
    /// </summary>
    Task<Workspace> CreateAsync(Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a workspace by identifier, or null if not found.
    /// </summary>
    Task<Workspace?> GetAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists workspaces owned by a given identity.
    /// </summary>
    Task<IReadOnlyList<Workspace>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all workspaces whose expiration time is at or before the given threshold.
    /// </summary>
    Task<IReadOnlyList<Workspace>> ListExpiredAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a workspace to a new lifecycle state.
    /// </summary>
    Task<bool> TransitionStateAsync(string workspaceId, WorkspaceLifecycleState newState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the expiration time of a workspace, clamped to the maximum allowed by policy.
    /// </summary>
    Task<bool> ExtendExpirationAsync(string workspaceId, DateTimeOffset newExpiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the workspace record from storage. Artifact cleanup is handled
    /// by the lifecycle orchestration layer before this call; implementations
    /// should not cascade-delete artifacts.
    /// </summary>
    Task<bool> DeleteAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregate counts and storage for a given owner, used for quota enforcement.
    /// </summary>
    Task<WorkspaceUsageSummary> GetUsageSummaryAsync(string ownerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregate workspace usage for quota evaluation.
/// </summary>
public sealed record WorkspaceUsageSummary
{
    /// <summary>
    /// Number of active workspaces.
    /// </summary>
    public int ActiveWorkspaceCount { get; init; }

    /// <summary>
    /// Total artifact count across active workspaces.
    /// </summary>
    public int TotalArtifactCount { get; init; }

    /// <summary>
    /// Total storage bytes across active workspaces.
    /// </summary>
    public long TotalStorageBytes { get; init; }
}
