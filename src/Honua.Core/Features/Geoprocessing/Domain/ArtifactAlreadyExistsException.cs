// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Raised when a geoprocessing output write would collide with an existing,
/// available artifact in the target workspace and the caller did not request
/// <c>env:overwriteOutput=true</c>. Mirrors arcpy's default
/// <c>arcpy.env.overwriteOutput = False</c> behavior: re-running a tool against
/// the same workspace output fails clearly instead of silently clobbering it.
/// </summary>
public sealed class ArtifactAlreadyExistsException : Exception
{
    /// <summary>
    /// Identifier of the workspace containing the colliding output.
    /// </summary>
    public string WorkspaceId { get; }

    /// <summary>
    /// Stable output label that already exists in the workspace.
    /// </summary>
    public string Label { get; }

    /// <summary>Creates a collision error for a workspace output label.</summary>
    public ArtifactAlreadyExistsException(string workspaceId, string label)
        : base(
            $"Output '{label}' already exists in workspace '{workspaceId}'. " +
            "Set env:overwriteOutput=true to replace it.")
    {
        WorkspaceId = workspaceId;
        Label = label;
    }
}
