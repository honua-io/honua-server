// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.


namespace Honua.Server.Features.AgentWorkspace;

internal interface ILocalProcessWorkspaceGateway
{
    bool IsConfigured { get; }
    Task<LocalProcessProjectIdentity> GetIdentityAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken);
    Task<LocalProcessProjectIdentity> BindAsync(LocalProcessWorkspaceAccess access, string projectId, CancellationToken cancellationToken);
    Task RevokeAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken);
    Task<LocalProcessProject> GetProjectAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken);
    Task<LocalProcessSourceFile> ReadSourceAsync(LocalProcessWorkspaceAccess access, string path, CancellationToken cancellationToken);
    Task<LocalProcessSourceWriteReceipt> WriteSourceAsync(LocalProcessWorkspaceAccess access, string path,
        string expectedSha256, LocalProcessSourceWrite request, CancellationToken cancellationToken);
    Task<LocalProcessSnapshot> SnapshotAsync(LocalProcessWorkspaceAccess access, LocalProcessSnapshotRequest request, CancellationToken cancellationToken);
    Task<LocalProcessSnapshot> GetSnapshotAsync(LocalProcessWorkspaceAccess access, string snapshotId, CancellationToken cancellationToken);
    Task<LocalProcessSourceFile> ReadSnapshotSourceAsync(LocalProcessWorkspaceAccess access, string snapshotId,
        string path, CancellationToken cancellationToken);
    Task<LocalProcessRun> RunAsync(LocalProcessWorkspaceAccess access, LocalProcessRunRequest request, CancellationToken cancellationToken);
    Task<LocalProcessRun> GetRunAsync(LocalProcessWorkspaceAccess access, string runId, CancellationToken cancellationToken);
    Task<LocalProcessRunList> ListRunsAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken);
    Task<LocalProcessRun> GetCommandAsync(LocalProcessWorkspaceAccess access, string commandId, CancellationToken cancellationToken);
    Task<LocalProcessRun> CancelAsync(LocalProcessWorkspaceAccess access, string runId, CancellationToken cancellationToken);
    Task<LocalProcessArtifact> ReadArtifactAsync(LocalProcessWorkspaceAccess access, string runId, string path, CancellationToken cancellationToken);
}
