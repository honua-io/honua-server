// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AgentWorkspace.Domain;

/// <summary>Trusted service assertion constructed from current authentication and durable workspace membership.</summary>
/// <param name="ActorId">Canonical requesting actor, never replaced with the owner.</param>
/// <param name="OwnerActorId">Persisted human workspace owner.</param>
/// <param name="WorkspaceId">Authorized durable workspace.</param>
/// <param name="TargetId">Canonical deployment identity, not a browser profile label.</param>
/// <param name="TenantId">Server-resolved tenant.</param>
/// <param name="Delegated">True for an authorized agent; never permits project binding or revocation.</param>
public sealed record LocalProcessWorkspaceAccess(string ActorId, string OwnerActorId, string WorkspaceId,
    string TargetId, string? TenantId, bool Delegated);

/// <summary>Persisted project ownership and one explicit workspace grant.</summary>
/// <param name="OwnerActorId">Canonical human owner.</param>
/// <param name="TenantId">Owner's trusted tenant.</param>
/// <param name="EnvironmentId">Canonical server target identity.</param>
/// <param name="WorkspaceId">Granted workspace identity.</param>
public sealed record LocalProcessProjectBinding(string OwnerActorId, string? TenantId, string EnvironmentId, string WorkspaceId);

/// <summary>Observed identity of the host-configured local project, without source bytes or filesystem locations.</summary>
/// <param name="ProjectId">Opaque daemon-issued identity.</param>
/// <param name="Revision">Monotonic project revision.</param>
/// <param name="SourceDigest">Digest of current source and input bytes.</param>
/// <param name="Image">Immutable sandbox image identity.</param>
/// <param name="Binding">Actual persisted ownership; null before a human binds the project.</param>
public sealed record LocalProcessProjectIdentity(string ProjectId, long Revision, string SourceDigest,
    string Image, LocalProcessProjectBinding? Binding);

/// <summary>One bounded, regular project source file and its concurrency token.</summary>
/// <param name="Path">Project-relative path; never an arbitrary host path.</param>
/// <param name="Content">Current UTF-8 source.</param>
/// <param name="Sha256">Digest used for conditional replacement.</param>
public sealed record LocalProcessSourceFile(string Path, string Content, string Sha256);

/// <summary>Explicit IDE handoff shown only to the authenticated human project owner.</summary>
/// <param name="Kind">Configured IDE protocol.</param>
/// <param name="Workspace">Host-selected project path.</param>
public sealed record LocalProcessIdeHandoff(string Kind, string Workspace);

/// <summary>Authorized editor projection of the actual configured project.</summary>
/// <param name="ProjectId">Opaque project identity.</param>
/// <param name="Revision">Current project revision.</param>
/// <param name="SourceDigest">Current source/input digest.</param>
/// <param name="Image">Immutable sandbox image.</param>
/// <param name="Binding">Verified ownership and workspace grant.</param>
/// <param name="Files">Bounded editable regular files.</param>
/// <param name="IdeHandoff">Human-only IDE location; absent for delegated requests.</param>
public sealed record LocalProcessProject(string ProjectId, long Revision, string SourceDigest, string Image,
    LocalProcessProjectBinding Binding, IReadOnlyList<LocalProcessSourceFile> Files, LocalProcessIdeHandoff? IdeHandoff = null);

/// <summary>Conditional source replacement; the file digest is supplied separately as If-Match.</summary>
/// <param name="ProjectId">Observed project identity.</param>
/// <param name="ExpectedRevision">Observed project revision.</param>
/// <param name="Content">Replacement UTF-8 source.</param>
public sealed record LocalProcessSourceWrite(string ProjectId, long ExpectedRevision, string Content);

/// <summary>Receipt for one accepted source replacement.</summary>
/// <param name="Path">Relative source path.</param>
/// <param name="Sha256">Resulting file digest.</param>
/// <param name="ProjectId">Actual project identity.</param>
/// <param name="Revision">Resulting project revision.</param>
/// <param name="SourceDigest">Resulting source/input digest.</param>
public sealed record LocalProcessSourceWriteReceipt(string Path, string Sha256, string ProjectId, long Revision, string SourceDigest);

/// <summary>Capture request bound to previously observed project bytes.</summary>
/// <param name="ProjectId">Actual project identity.</param>
/// <param name="ExpectedRevision">Observed project revision.</param>
/// <param name="SourceDigest">Observed source/input digest.</param>
public sealed record LocalProcessSnapshotRequest(string ProjectId, long ExpectedRevision, string SourceDigest);

/// <summary>Immutable source/input snapshot retained by the existing sandbox adapter.</summary>
/// <param name="ProjectId">Owning project.</param>
/// <param name="SnapshotId">Opaque retained snapshot identity.</param>
/// <param name="SnapshotDigest">Actual retained byte digest.</param>
/// <param name="Image">Exact sandbox image.</param>
/// <param name="Revision">Captured project revision.</param>
public sealed record LocalProcessSnapshot(string ProjectId, string SnapshotId, string SnapshotDigest, string Image, long Revision);

/// <summary>Idempotent submission of one exact retained snapshot to the existing executor.</summary>
/// <param name="ProjectId">Owning project.</param>
/// <param name="SnapshotId">Previously captured snapshot.</param>
/// <param name="SnapshotDigest">Exact captured bytes.</param>
/// <param name="CommandId">Stable command identity retained across uncertain responses.</param>
public sealed record LocalProcessRunRequest(string ProjectId, string SnapshotId, string SnapshotDigest, string CommandId);

/// <summary>Declared artifact verified against the successful harness receipt.</summary>
/// <param name="Path">Run-relative artifact path.</param>
/// <param name="Bytes">Verified byte length.</param>
/// <param name="Sha256">Verified artifact digest.</param>
public sealed record LocalProcessArtifactReference(string Path, long Bytes, string Sha256);

/// <summary>Canonical local execution receipt; never a server workflow-package or imported-layer identity.</summary>
public sealed record LocalProcessRun
{
    /// <summary>Opaque actual project identity.</summary>
    public required string ProjectId { get; init; }
    /// <summary>Retained snapshot identity.</summary>
    public required string SnapshotId { get; init; }
    /// <summary>Retained snapshot byte digest.</summary>
    public required string SnapshotDigest { get; init; }
    /// <summary>Durable local execution identity.</summary>
    public required string RunId { get; init; }
    /// <summary>Observed execution state.</summary>
    public required string Status { get; init; }
    /// <summary>Exact sandbox image.</summary>
    public required string Image { get; init; }
    /// <summary>Sanitized, bounded execution diagnostics.</summary>
    public string? Diagnostics { get; init; }
    /// <summary>Actual process exit code when available.</summary>
    public int? ExitCode { get; init; }
    /// <summary>Observed execution duration when available.</summary>
    public long? ElapsedMs { get; init; }
    /// <summary>True when cancellation was accepted but may not yet be terminal.</summary>
    public bool? CancellationRequested { get; init; }
    /// <summary>Verified successful output references.</summary>
    public IReadOnlyList<LocalProcessArtifactReference> Artifacts { get; init; } = [];
}

/// <summary>Bounded verified artifact bytes for the normal import pipeline.</summary>
/// <param name="Reference">Canonical declared artifact identity.</param>
/// <param name="Content">Actual bytes; consumers must not treat a local artifact as an imported layer.</param>
public sealed record LocalProcessArtifact(LocalProcessArtifactReference Reference, byte[] Content);

/// <summary>Bounded newest-first execution discovery under the current project and workspace grant.</summary>
/// <param name="Runs">At most 32 authoritative local run receipts.</param>
public sealed record LocalProcessRunList(IReadOnlyList<LocalProcessRun> Runs);
