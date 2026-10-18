// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed class DatasetImportAuthorityDescriptorReader(IAgentWorkspaceStore store, IDatasetImportTaskProvider tasks,
    IHttpContextAccessor accessor) : IDatasetImportAuthorityDescriptorReader
{
    public async Task<DatasetImportAuthorityDescriptor?> ReadAsync(ClaimsPrincipal caller, string workspaceId, string taskId,
        long revision, string contentHash, CancellationToken cancellationToken = default)
    {
        var transport = accessor.HttpContext;
        var actor = CanonicalSecurityActor.Resolve(caller)?.ActorId;
        if (transport is null || actor is null || actor != CanonicalSecurityActor.Resolve(transport.User)?.ActorId) return null;
        var task = await AuthorizeAsync(caller, workspaceId, taskId, revision, contentHash, cancellationToken).ConfigureAwait(false);
        if (task is null) return null;
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (document is null || !document.DatasetImportExecutionBindings.TryGetValue(taskId, out var binding) ||
            binding.TaskRevision != revision || binding.TaskContentHash != contentHash || binding.ExecutorActorId != task.OwnerActorId ||
            (binding.ExecutorActorId != actor && (binding.InitiatingHumanActorId != actor || document.OwnerId != actor))) return null;
        var prepared = document.DatasetImportPreparedAdmissions.GetValueOrDefault(taskId);
        if (prepared is not null && (prepared.TaskRevision != revision || prepared.TaskContentHash != contentHash ||
            prepared.AuthorityRevision != binding.AuthorityRevision || prepared.AuthorityFingerprint != binding.AuthorityFingerprint ||
            prepared.PolicyContextFingerprint != binding.PolicyContextFingerprint || prepared.AcceptBefore != task.UploadAdmissionDeadline ||
            prepared.ExecutorActorId != binding.ExecutorActorId || prepared.InitiatingHumanActorId != binding.InitiatingHumanActorId ||
            prepared.OriginatingTurnId != binding.OriginatingTurnId || prepared.OriginalTurnHash != binding.OriginalTurnHash)) return null;
        if (await AuthorizeAsync(caller, workspaceId, taskId, revision, contentHash, cancellationToken).ConfigureAwait(false) is null) return null;
        var latest = await store.GetAsync(workspaceId).ConfigureAwait(false);
        var target = await store.GetTargetIdAsync().ConfigureAwait(false);
        if (latest is null || latest.OwnerId != document.OwnerId || latest.AgentActorId != document.AgentActorId || target != document.TargetId ||
            !AgentWorkspaceState.CanRead(latest, actor, transport.RequestServices.GetService<ITenantContext>()?.TenantId, target) ||
            !latest.DatasetImportTasks.TryGetValue(taskId, out var retainedTasks) ||
            retainedTasks.SingleOrDefault(candidate => candidate.Revision == revision) is not { } retainedTask || retainedTask != task ||
            !latest.DatasetImportExecutionBindings.TryGetValue(taskId, out var retained) || retained.AuthorityRevision != binding.AuthorityRevision ||
            retained.AuthorityFingerprint != binding.AuthorityFingerprint || retained.PolicyContextFingerprint != binding.PolicyContextFingerprint ||
            retained.TaskId != binding.TaskId || retained.TaskRevision != binding.TaskRevision || retained.TaskContentHash != binding.TaskContentHash ||
            retained.ExecutorActorId != binding.ExecutorActorId || retained.InitiatingHumanActorId != binding.InitiatingHumanActorId ||
            retained.OriginatingTurnId != binding.OriginatingTurnId || retained.OriginalTurnHash != binding.OriginalTurnHash ||
            retained.ExecutorAuthority.TokenExpiresAt != binding.ExecutorAuthority.TokenExpiresAt ||
            !SamePreparation(prepared, latest.DatasetImportPreparedAdmissions.GetValueOrDefault(taskId)) ||
            !await AgentCredentialAuthority.IsCurrentAsync(caller, transport.RequestServices, cancellationToken).ConfigureAwait(false)) return null;
        return new(taskId, revision, contentHash, binding.ExecutorActorId, binding.InitiatingHumanActorId, binding.OriginatingTurnId,
            binding.OriginalTurnHash, binding.AuthorityRevision, binding.AuthorityFingerprint, binding.PolicyContextFingerprint,
            task.UploadAdmissionDeadline, prepared?.OperationId);
    }

    private static bool SamePreparation(DatasetImportPreparedAdmission? original, DatasetImportPreparedAdmission? retained)
    {
        if (original is null) return retained is null;
        if (retained is null) return false;
        var metadata = AgentWorkspaceJsonContext.Default.GetTypeInfo(typeof(DatasetImportPreparedAdmission));
        return metadata is not null && JsonSerializer.SerializeToUtf8Bytes(original, metadata).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(retained, metadata));
    }

    private async Task<DatasetImportTask?> AuthorizeAsync(ClaimsPrincipal caller, string workspaceId, string taskId,
        long revision, string hash, CancellationToken cancellationToken)
    {
        var owned = await tasks.AuthorizeAsync(caller, workspaceId, taskId, revision, hash, false, cancellationToken).ConfigureAwait(false);
        if (owned is not null) return owned;
        var contributors = accessor.HttpContext?.RequestServices.GetService<IDatasetImportContributorAuthorizer>();
        return contributors is null ? null : (await contributors.AuthorizeAsync(caller, workspaceId, taskId, revision, hash,
            false, cancellationToken).ConfigureAwait(false))?.Task;
    }
}
