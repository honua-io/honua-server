// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed class DatasetImportAdmissionProvider(IAgentWorkspaceStore store, IDatasetImportTaskProvider tasks,
    IHttpContextAccessor accessor) : IDatasetImportAdmissionProvider
{
    public async Task<DatasetImportPreparedAdmission?> ReadAsync(ClaimsPrincipal executor, string workspaceId,
        string taskId, long taskRevision, string taskContentHash, CancellationToken cancellationToken = default)
    {
        if (await tasks.AuthorizeAsync(executor, workspaceId, taskId, taskRevision, taskContentHash, false, cancellationToken).ConfigureAwait(false) is null)
            return null;
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (document is null || !document.DatasetImportPreparedAdmissions.TryGetValue(taskId, out var prepared) ||
            prepared.TaskRevision != taskRevision || prepared.TaskContentHash != taskContentHash ||
            prepared.ExecutorActorId != CanonicalSecurityActor.Resolve(executor)?.ActorId) return null;
        return await CurrentAsync(executor, document, cancellationToken).ConfigureAwait(false) ? prepared : null;
    }

    public async Task<DatasetImportPreparedAdmission?> PrepareAsync(ClaimsPrincipal executor, string workspaceId,
        string taskId, long taskRevision, string taskContentHash, long authorityRevision, string authorityFingerprint,
        CancellationToken cancellationToken = default)
    {
        // Existing preparation is observation: it must survive its own destination creation.
        var original = await ReadAsync(executor, workspaceId, taskId, taskRevision, taskContentHash, cancellationToken).ConfigureAwait(false);
        if (original is not null)
        {
            if (original.AuthorityRevision != authorityRevision || original.AuthorityFingerprint != authorityFingerprint)
                throw new AgentWorkspaceConflictException("The original import admission has a different sealed authority.");
            return original;
        }
        var task = await tasks.AuthorizeAsync(executor, workspaceId, taskId, taskRevision, taskContentHash, true, cancellationToken).ConfigureAwait(false);
        if (task is null || task.Source.ExpectedSha256 is null) return null;
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        var services = accessor.HttpContext?.RequestServices;
        if (document is null || services is null || !document.DatasetImportExecutionBindings.TryGetValue(taskId, out var binding) ||
            binding.TaskRevision != taskRevision || binding.TaskContentHash != taskContentHash ||
            binding.AuthorityRevision != authorityRevision || binding.AuthorityFingerprint != authorityFingerprint ||
            binding.PolicyContextFingerprint != JobSecurityContextFingerprint.Compute(binding.ExecutorPolicyContext) ||
            binding.ExecutorActorId != task.OwnerActorId || binding.ExecutorActorId != CanonicalSecurityActor.Resolve(executor)?.ActorId ||
            (binding.InitiatingHumanActorId is not null && (document.OwnerId != binding.InitiatingHumanActorId || document.AgentActorId != binding.ExecutorActorId)) ||
            !document.Turns.TryGetValue(binding.OriginatingTurnId, out var turn) ||
            binding.OriginalTurnHash != Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn))) ||
            task.UploadAdmissionDeadline <= services.GetRequiredService<TimeProvider>().GetUtcNow() ||
            await AgentCredentialAuthority.RevalidateAsync(binding.ExecutorAuthority, services, cancellationToken).ConfigureAwait(false) is null ||
            !await CurrentAsync(executor, document, cancellationToken).ConfigureAwait(false)) return null;
        if (document.DatasetImportPreparedAdmissions.ContainsKey(taskId))
            throw new AgentWorkspaceConflictException("Import admission changed concurrently. Read the original preparation.");
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { document.TargetId, document.WorkspaceId, taskId,
            taskRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), taskContentHash, task.OwnerActorId }, AgentWorkspaceJsonContext.Default.StringArray);
        var prepared = new DatasetImportPreparedAdmission(taskId, taskRevision, taskContentHash,
            "dataset-import-" + Convert.ToHexStringLower(SHA256.HashData(identity)), binding.AuthorityRevision,
            binding.AuthorityFingerprint, binding.ExecutorActorId, binding.InitiatingHumanActorId, binding.OriginatingTurnId,
            binding.OriginalTurnHash, binding.ExecutorPolicyContext with { AgentCredential = binding.ExecutorAuthority },
            binding.PolicyContextFingerprint, binding.ExecutorAuthority, task.UploadAdmissionDeadline);
        var preparations = new Dictionary<string, DatasetImportPreparedAdmission>(document.DatasetImportPreparedAdmissions, StringComparer.Ordinal)
        { [taskId] = prepared };
        if (!await store.SaveAsync(document with { DatasetImportPreparedAdmissions = preparations }).ConfigureAwait(false))
            throw new AgentWorkspaceConflictException("Import authority changed concurrently. Read the original preparation before retrying.");
        return prepared;
    }

    private async Task<bool> CurrentAsync(ClaimsPrincipal executor, AgentWorkspaceDocument document, CancellationToken cancellationToken)
    {
        var services = accessor.HttpContext?.RequestServices;
        if (services is null) return false;
        var latest = await store.GetAsync(document.WorkspaceId).ConfigureAwait(false);
        var target = await store.GetTargetIdAsync().ConfigureAwait(false);
        return latest is not null && latest.Version == document.Version && latest.OwnerId == document.OwnerId &&
            latest.AgentActorId == document.AgentActorId && target == document.TargetId &&
            AgentWorkspaceState.CanRead(latest, CanonicalSecurityActor.Resolve(executor)?.ActorId ?? string.Empty,
                services.GetService<Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext>()?.TenantId, target) &&
            await AgentCredentialAuthority.IsCurrentAsync(executor, services, cancellationToken).ConfigureAwait(false);
    }
}
