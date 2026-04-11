// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Reference to a managed working-state container used during geoprocessing.
/// </summary>
public sealed record WorkspaceRef
{
    /// <summary>
    /// Unique identifier for this workspace.
    /// </summary>
    public required string WorkspaceId { get; init; }

    /// <summary>
    /// Lifetime class of the workspace.
    /// </summary>
    public required WorkspaceKind Kind { get; init; }

    /// <summary>
    /// Human-readable name for the workspace.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Location of the workspace when materialized.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// When the workspace expires, for temporary or scratch workspaces.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
