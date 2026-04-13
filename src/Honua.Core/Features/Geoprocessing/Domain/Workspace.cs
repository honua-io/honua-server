// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Managed working-state container with lifecycle tracking for geoprocessing workflows.
/// </summary>
public sealed record Workspace
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
    /// Identity of the workspace owner.
    /// </summary>
    public required string OwnerId { get; init; }

    /// <summary>
    /// Optional scope restricting workspace visibility (service, organization, etc.).
    /// </summary>
    public string? ScopeId { get; init; }

    /// <summary>
    /// Current lifecycle state.
    /// </summary>
    public required WorkspaceLifecycleState State { get; init; }

    /// <summary>
    /// Location of the workspace when materialized.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// When the workspace was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the workspace expires. Null means no automatic expiration.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Artifacts stored in this workspace.
    /// </summary>
    public IReadOnlyList<Artifact> Artifacts { get; init; } = [];

    /// <summary>
    /// Total storage bytes consumed by artifacts in this workspace.
    /// </summary>
    public long StorageBytes { get; init; }

    /// <summary>
    /// Creates a lightweight reference to this workspace.
    /// </summary>
    public WorkspaceRef ToRef() => new()
    {
        WorkspaceId = WorkspaceId,
        Kind = Kind,
        Label = Label,
        Uri = Uri,
        ExpiresAt = ExpiresAt
    };

    /// <summary>
    /// Returns whether this workspace has passed its expiration time relative to the given clock.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) =>
        ExpiresAt.HasValue && now >= ExpiresAt.Value;
}

/// <summary>
/// A materialized artifact within a workspace, with lifecycle and storage tracking.
/// </summary>
public sealed record Artifact
{
    /// <summary>
    /// Unique identifier for this artifact.
    /// </summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Category of the artifact.
    /// </summary>
    public required ArtifactKind Kind { get; init; }

    /// <summary>
    /// Human-readable name for the artifact.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Current lifecycle state.
    /// </summary>
    public required ArtifactLifecycleState State { get; init; }

    /// <summary>
    /// Location of the artifact when materialized.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// MIME type of the artifact content when applicable.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Size of the artifact in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// When the artifact was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Opaque metadata associated with the artifact.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Identifier of the workspace that owns this artifact.
    /// </summary>
    public required string WorkspaceId { get; init; }

    /// <summary>
    /// Creates a lightweight reference to this artifact.
    /// </summary>
    public ArtifactRef ToRef() => new()
    {
        ArtifactId = ArtifactId,
        Kind = Kind,
        Label = Label,
        Uri = Uri,
        ContentType = ContentType,
        Metadata = Metadata
    };
}
