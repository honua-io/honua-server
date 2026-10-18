// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AgentWorkspace.Domain;

namespace Honua.Core.Features.AgentWorkspace.Abstractions;

/// <summary>Typed authorized transport to the existing host-pinned local source and sandbox adapter.</summary>
public interface ILocalProcessWorkspaceGateway
{
    /// <summary>Whether the host configured a private daemon and its independent service credential.</summary>
    bool IsConfigured { get; }
    /// <summary>Read current project references without source or IDE locations.</summary>
    Task<LocalProcessProjectIdentity> GetIdentityAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken);
    /// <summary>Bind the host-selected project to an actual human-owned workspace; agents cannot bind.</summary>
    Task<LocalProcessProjectIdentity> BindAsync(LocalProcessWorkspaceAccess access, string projectId, CancellationToken cancellationToken);
    /// <summary>Revoke the current workspace grant as its human owner.</summary>
    Task RevokeAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken);
    /// <summary>Read the authorized project editor projection.</summary>
    Task<LocalProcessProject> GetProjectAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken);
    /// <summary>Read one bounded, regular, relative source file.</summary>
    Task<LocalProcessSourceFile> ReadSourceAsync(LocalProcessWorkspaceAccess access, string path, CancellationToken cancellationToken);
    /// <summary>Replace existing source only when the current file and project revisions match.</summary>
    Task<LocalProcessSourceWriteReceipt> WriteSourceAsync(LocalProcessWorkspaceAccess access, string path,
        string expectedSha256, LocalProcessSourceWrite request, CancellationToken cancellationToken);
    /// <summary>Retain immutable source/input bytes matching the observed revision and digest.</summary>
    Task<LocalProcessSnapshot> SnapshotAsync(LocalProcessWorkspaceAccess access, LocalProcessSnapshotRequest request, CancellationToken cancellationToken);
    /// <summary>Read an authorized retained snapshot's immutable identity.</summary>
    Task<LocalProcessSnapshot> GetSnapshotAsync(LocalProcessWorkspaceAccess access, string snapshotId, CancellationToken cancellationToken);
    /// <summary>Read immutable source captured for a prompt without silently selecting later edits.</summary>
    Task<LocalProcessSourceFile> ReadSnapshotSourceAsync(LocalProcessWorkspaceAccess access, string snapshotId,
        string path, CancellationToken cancellationToken);
    /// <summary>Submit the exact retained snapshot with durable command deduplication.</summary>
    Task<LocalProcessRun> RunAsync(LocalProcessWorkspaceAccess access, LocalProcessRunRequest request, CancellationToken cancellationToken);
    /// <summary>Read the canonical local run receipt under current ownership and workspace authorization.</summary>
    Task<LocalProcessRun> GetRunAsync(LocalProcessWorkspaceAccess access, string runId, CancellationToken cancellationToken);
    /// <summary>Resolve a durable command before retrying a submission whose response was lost.</summary>
    Task<LocalProcessRun> GetCommandAsync(LocalProcessWorkspaceAccess access, string commandId, CancellationToken cancellationToken);
    /// <summary>Request cancellation of an authorized local run, preserving terminal outcomes.</summary>
    Task<LocalProcessRun> CancelAsync(LocalProcessWorkspaceAccess access, string runId, CancellationToken cancellationToken);
    /// <summary>Read bounded successful output and recheck its canonical length and digest.</summary>
    Task<LocalProcessArtifact> ReadArtifactAsync(LocalProcessWorkspaceAccess access, string runId, string path, CancellationToken cancellationToken);
}
