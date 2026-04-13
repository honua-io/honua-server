// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Summary of a workspace cleanup sweep.
/// </summary>
public sealed record CleanupResult
{
    /// <summary>
    /// Number of workspaces transitioned to expired state.
    /// </summary>
    public int WorkspacesExpired { get; init; }

    /// <summary>
    /// Number of expired workspaces whose storage was reclaimed.
    /// </summary>
    public int WorkspacesDeleted { get; init; }

    /// <summary>
    /// Number of individual artifacts deleted.
    /// </summary>
    public int ArtifactsDeleted { get; init; }

    /// <summary>
    /// Total storage bytes reclaimed.
    /// </summary>
    public long BytesReclaimed { get; init; }

    /// <summary>
    /// Errors encountered during cleanup that did not halt the sweep.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// An empty result indicating no cleanup was performed.
    /// </summary>
    public static CleanupResult None { get; } = new();
}
