// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.AgentWorkspace;

internal sealed record LocalProcessWorkspaceAccess(string ActorId, string OwnerActorId, string WorkspaceId,
    string TargetId, string? TenantId, bool Delegated);

internal sealed record LocalProcessProjectBinding(string OwnerActorId, string? TenantId, string EnvironmentId, string WorkspaceId);

internal sealed record LocalProcessProjectIdentity(string ProjectId, long Revision, string SourceDigest,
    string Image, LocalProcessProjectBinding? Binding);

internal sealed record LocalProcessSourceFile(string Path, string Content, string Sha256);

internal sealed record LocalProcessIdeHandoff(string Kind, string Workspace);

internal sealed record LocalProcessProject(string ProjectId, long Revision, string SourceDigest, string Image,
    LocalProcessProjectBinding Binding, IReadOnlyList<LocalProcessSourceFile> Files, LocalProcessIdeHandoff? IdeHandoff = null);

internal sealed record LocalProcessSourceWrite(string ProjectId, long ExpectedRevision, string Content);

internal sealed record LocalProcessSourceWriteReceipt(string Path, string Sha256, string ProjectId, long Revision, string SourceDigest);

internal sealed record LocalProcessSnapshotRequest(string ProjectId, long ExpectedRevision, string SourceDigest);

internal sealed record LocalProcessSnapshot(string ProjectId, string SnapshotId, string SnapshotDigest, string Image, long Revision);

internal sealed record LocalProcessRunRequest(string ProjectId, string SnapshotId, string SnapshotDigest, string CommandId);

internal sealed record LocalProcessArtifactReference(string Path, long Bytes, string Sha256);

internal sealed record LocalProcessRun
{
    public required string ProjectId { get; init; }
    public required string SnapshotId { get; init; }
    public required string SnapshotDigest { get; init; }
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public required string Image { get; init; }
    public string? Diagnostics { get; init; }
    public int? ExitCode { get; init; }
    public long? ElapsedMs { get; init; }
    public bool? CancellationRequested { get; init; }
    public IReadOnlyList<LocalProcessArtifactReference> Artifacts { get; init; } = [];
}

internal sealed record LocalProcessArtifact(LocalProcessArtifactReference Reference, byte[] Content);

internal sealed record LocalProcessRunList(IReadOnlyList<LocalProcessRun> Runs);
