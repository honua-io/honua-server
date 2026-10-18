// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.AgentWorkspace;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>Thin immutable authoring adapter over canonical package validation and persistence.</summary>
internal sealed class WorkflowPackageAuthoring(WorkflowPackageService packages, WorkflowPackageAuthorization authorization, IAgentWorkspaceStore workspaces)
    : IWorkflowPackageAuthoring
{
    public async Task<IReadOnlyList<WorkflowPackage>> ListAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        CancellationToken cancellationToken)
    {
        await RequireWorkspaceAsync(principal, workspaceId, turnId, false, cancellationToken).ConfigureAwait(false);
        return await packages.ListPackagesAsync(principal, cancellationToken, workspaceId, turnId).ConfigureAwait(false);
    }

    public async Task<WorkflowPackageVersion?> ReadAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        string packageId, int version, CancellationToken cancellationToken)
    {
        await RequireWorkspaceAsync(principal, workspaceId, turnId, false, cancellationToken).ConfigureAwait(false);
        return await packages.GetVersionAsync(packageId, version, principal, cancellationToken, workspaceId, turnId).ConfigureAwait(false);
    }

    public async Task<WorkflowPackageVersion> CreateAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        string commandId, string name, WorkflowGraph graph, CancellationToken cancellationToken)
        => (await CreateAndAttachAsync(principal, workspaceId, turnId, commandId, name, graph, cancellationToken).ConfigureAwait(false)).Version;

    public async Task<WorkflowPackageAuthoringResult> CreateAndAttachAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        string commandId, string name, WorkflowGraph graph, CancellationToken cancellationToken)
    {
        if (!AgentWorkspaceState.IsIdentifier(commandId) || string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new GeoprocessingValidationException("A bounded commandId and workflow name are required.");
        var fingerprint = WorkflowPackageContent.Fingerprint(name, graph);
        var hash = WorkflowPackageGraphHasher.Compute(graph);
        AgentWorkflowAuthoringCommand? reserved = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var access = await RequireWorkspaceAsync(principal, workspaceId, turnId, true, cancellationToken).ConfigureAwait(false);
            var binding = access.TurnBinding!;
            var document = await workspaces.GetAsync(workspaceId).ConfigureAwait(false);
            if (document is null || document.OwnerId != access.DelegatedOwnerActorId || !AgentWorkspaceState.CanRead(document, access.ActorId, access.TenantId,
                    await workspaces.GetTargetIdAsync().ConfigureAwait(false)))
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            if (document.WorkflowAuthoringCommands.TryGetValue(commandId, out reserved))
            {
                if (reserved.ActorId != access.ActorId || reserved.WorkspaceId != workspaceId || reserved.TurnId != turnId ||
                    reserved.RequestFingerprint != fingerprint || reserved.PackageHash != hash || reserved.ExpectedVersion != 1 ||
                    reserved.OwnerActorId != access.DelegatedOwnerActorId || reserved.TenantId != access.TenantId ||
                    reserved.TargetId != binding.TargetId || reserved.ReleaseId != binding.ReleaseId)
                    throw new GeoprocessingPreconditionFailedException("Workflow command is already bound to different input or authority.");
                break;
            }
            if (document.WorkflowAuthoringCommands.Count >= 256)
                throw new GeoprocessingPreconditionFailedException("Workspace workflow command limit reached.");
            if (!document.Turns.TryGetValue(turnId, out var capturedTurn))
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            reserved = new(commandId, access.ActorId, workspaceId, turnId, fingerprint,
                $"agent-workflow-{Guid.NewGuid():N}", 1, hash, access.DelegatedOwnerActorId!, access.TenantId!, binding.TargetId, binding.ReleaseId,
                capturedTurn.Context.ViewId, capturedTurn.Context.Revision);
            var commands = new Dictionary<string, AgentWorkflowAuthoringCommand>(document.WorkflowAuthoringCommands, StringComparer.Ordinal)
            { [commandId] = reserved };
            if (await workspaces.SaveAsync(document with { WorkflowAuthoringCommands = commands }).ConfigureAwait(false)) break;
            reserved = null;
        }
        if (reserved is null) throw new GeoprocessingPreconditionFailedException("Concurrent workspace mutation; retry the same command.");
        // The command tuple is durable before the first package write. Atomic store creation
        // fixes version1 to the requested bytes; neither retry nor concurrent human edits upsert it.
        var version = await packages.CreateInitialAuthoringVersionAsync(reserved, name, graph, principal, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var access = await RequireWorkspaceAsync(principal, workspaceId, turnId, true, cancellationToken).ConfigureAwait(false);
            var document = await workspaces.GetAsync(workspaceId).ConfigureAwait(false);
            if (document is null || document.OwnerId != access.DelegatedOwnerActorId ||
                !AgentWorkspaceState.CanRead(document, access.ActorId, access.TenantId, await workspaces.GetTargetIdAsync().ConfigureAwait(false)) ||
                !document.WorkflowAuthoringCommands.TryGetValue(commandId, out var command) || command != reserved)
            {
                // Another retry may already have atomically attached the same immutable artifact.
                if (document is not null && document.WorkflowAuthoringCommands.TryGetValue(commandId, out var completed) &&
                    (completed with { AttachmentState = reserved.AttachmentState, AttachedRevision = reserved.AttachedRevision }) == reserved &&
                    completed.AttachmentState is "Attached" or "Conflict" && document.OwnerId == access.DelegatedOwnerActorId &&
                    AgentWorkspaceState.CanRead(document, access.ActorId, access.TenantId, await workspaces.GetTargetIdAsync().ConfigureAwait(false)))
                    return new(version, completed.AttachmentState, completed.AttachedRevision);
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            }
            if (command.AttachmentState is "Attached" or "Conflict")
                return new(version, command.AttachmentState, command.AttachedRevision);
            var updated = AgentWorkspaceState.AttachWorkflowArtifact(document, command, version.PackageId, version.Version, version.PackageHash);
            if (await workspaces.SaveAsync(updated).ConfigureAwait(false))
            {
                var outcome = updated.WorkflowAuthoringCommands[commandId];
                return new(version, outcome.AttachmentState, outcome.AttachedRevision);
            }
        }
        throw new GeoprocessingPreconditionFailedException("Concurrent workspace mutation; retry the same command to reconcile attachment.");
    }

    private async Task<WorkflowPackageAccess> RequireWorkspaceAsync(ClaimsPrincipal principal, string workspaceId, string turnId, bool write,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(turnId))
            throw new GeoprocessingValidationException("An explicit workspace and frozen turn are required.");
        var access = await authorization.ResolveAsync(principal, cancellationToken, workspaceId, turnId, write).ConfigureAwait(false);
        if (!access.WorkspaceAgent || string.IsNullOrWhiteSpace(access.DelegatedOwnerActorId) || access.TurnBinding is null)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        return access;
    }
}
