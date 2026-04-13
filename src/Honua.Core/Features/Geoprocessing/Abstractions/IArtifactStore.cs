// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Storage operations for artifact lifecycle management within workspaces.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Adds an artifact to a workspace.
    /// </summary>
    Task<Artifact> CreateAsync(Artifact artifact, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an artifact by identifier, or null if not found.
    /// </summary>
    Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all artifacts in a workspace.
    /// </summary>
    Task<IReadOnlyList<Artifact>> ListByWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions an artifact to a new lifecycle state.
    /// </summary>
    Task<bool> TransitionStateAsync(string artifactId, ArtifactLifecycleState newState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an artifact and reclaims its storage.
    /// </summary>
    Task<bool> DeleteAsync(string artifactId, CancellationToken cancellationToken = default);
}
