// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal static class DatasetImportAuthorityRenewalService
{
    internal static async Task<DatasetImportAuthorityRenewal?> RenewAsync(HttpContext context, IAgentWorkspaceStore store,
        string workspaceId, string taskId, long taskRevision, RenewDatasetImportAuthority request)
    {
        if (!AgentWorkspaceState.IsIdentifier(request.RenewalId) || request.ExpectedAuthorityRevision < 1)
            throw new ArgumentException("A valid immutable renewal identity and authority revision are required.");
        var tasks = context.RequestServices.GetRequiredService<IDatasetImportTaskProvider>();
        if (await tasks.AuthorizeAsync(context.User, workspaceId, taskId, taskRevision, request.TaskContentHash,
                false, context.RequestAborted).ConfigureAwait(false) is null) return null;
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (document is null || !document.DatasetImportExecutionBindings.TryGetValue(taskId, out var binding) ||
            binding.TaskRevision != taskRevision || binding.TaskContentHash != request.TaskContentHash || binding.ExecutorActorId != actor)
            return null;
        AgentWorkspaceCapturedContext.ReadBoundTurn(context, document);
        var renewalKey = DatasetImportTaskState.RenewalKey(taskId, taskRevision, request.TaskContentHash, request.RenewalId);
        // Recover the persisted original receipt before comparing a newly refreshed token.
        // The retry's token can be newer than the token that performed this renewal.
        if (document.DatasetImportAuthorityRenewals.TryGetValue(renewalKey, out var original))
        {
            if (original.RenewalId != request.RenewalId || original.PreviousAuthorityRevision != request.ExpectedAuthorityRevision)
                throw new AgentWorkspaceConflictException("The original renewal has different immutable inputs.");
            return await CanReturnAsync(context, store, document, taskId, taskRevision, request.TaskContentHash).ConfigureAwait(false)
                ? original : null;
        }
        if (document.DatasetImportPreparedAdmissions.ContainsKey(taskId))
            throw new AgentWorkspaceConflictException("Import admission already sealed the executor authority. Task renewal cannot extend an accepted job.");
        if (binding.AuthorityRevision != request.ExpectedAuthorityRevision)
            throw new AgentWorkspaceConflictException("The task authority changed. Read its current revision before renewal.");
        if (document.DatasetImportAuthorityRenewals.Count >= 1024)
            throw new AgentWorkspaceConflictException("The workspace authority renewal limit was reached.");
        var task = await tasks.AuthorizeAsync(context.User, workspaceId, taskId, taskRevision, request.TaskContentHash,
            true, context.RequestAborted).ConfigureAwait(false);
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (task is null || task.UploadAdmissionDeadline <= now ||
            !document.Turns.TryGetValue(binding.OriginatingTurnId, out var turn) ||
            binding.OriginalTurnHash != Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn))) ||
            (binding.InitiatingHumanActorId is not null && (document.OwnerId != binding.InitiatingHumanActorId || document.AgentActorId != actor)))
            return null;
        var authorizedTurn = await context.RequestServices.GetRequiredService<Honua.Core.Features.AgentWorkspace.Abstractions.IAgentWorkspaceTurnAuthorizer>()
            .AuthorizeAsync(context.User, workspaceId, binding.OriginatingTurnId, context.RequestAborted).ConfigureAwait(false);
        if (authorizedTurn is null || authorizedTurn.OwnerActorId != document.OwnerId ||
            authorizedTurn.TargetId != document.TargetId || authorizedTurn.TenantId != document.TenantId) return null;
        var policyCapture = context.RequestServices.GetService<Honua.Core.Features.Authorization.Abstractions.IAgentJobSecurityContextCapture>();
        if (policyCapture is null) return null;
        JobSecurityContext freshPolicy;
        try { freshPolicy = await policyCapture.CaptureCurrentAsync(context.RequestAborted).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { return null; }
        var fresh = freshPolicy.AgentCredential;
        // Compare canonical renewal lineage, retaining every original policy value.
        // Refresh metadata is never substituted into the frozen policy context.
        if (fresh is null || !DatasetImportExecutionAuthority.SameLineage(binding.ExecutorAuthority, fresh) ||
            binding.PolicyContextFingerprint != JobSecurityContextFingerprint.Compute(binding.ExecutorPolicyContext) ||
            JobSecurityContextFingerprint.ComputeRenewalLineage(binding.ExecutorPolicyContext) != JobSecurityContextFingerprint.ComputeRenewalLineage(freshPolicy)) return null;
        if (fresh.TokenExpiresAt <= binding.ExecutorAuthority.TokenExpiresAt)
            throw new AgentWorkspaceConflictException("Renewal requires a refreshed expiry under the same executor authority.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fresh, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot)));
        var nextRevision = checked(binding.AuthorityRevision + 1);
        var receipt = new DatasetImportAuthorityRenewal(request.RenewalId, binding.AuthorityRevision, nextRevision, fingerprint, now);
        var bindings = new Dictionary<string, DatasetImportExecutionBinding>(document.DatasetImportExecutionBindings, StringComparer.Ordinal)
        { [taskId] = binding with { AuthorityRevision = nextRevision, AuthorityFingerprint = fingerprint, ExecutorAuthority = fresh } };
        var renewals = new Dictionary<string, DatasetImportAuthorityRenewal>(document.DatasetImportAuthorityRenewals, StringComparer.Ordinal)
        { [renewalKey] = receipt };
        if (!await CanReturnAsync(context, store, document, taskId, taskRevision, request.TaskContentHash, true).ConfigureAwait(false)) return null;
        // Admission preparation and renewal both compare-and-set this same document.
        // A losing writer cannot replace a sealed snapshot or advance authority twice.
        if (!await store.SaveAsync(document with { DatasetImportExecutionBindings = bindings, DatasetImportAuthorityRenewals = renewals }).ConfigureAwait(false))
            throw new AgentWorkspaceConflictException("Import admission or authority changed concurrently. Recover the original renewal receipt before retrying.");
        return await CanReturnAsync(context, store, document, taskId, taskRevision, request.TaskContentHash).ConfigureAwait(false)
            ? receipt : null;
    }

    private static async Task<bool> CanReturnAsync(HttpContext context, IAgentWorkspaceStore store,
        AgentWorkspaceDocument original, string taskId, long revision, string contentHash, bool requireWrite = false)
    {
        var task = await context.RequestServices.GetRequiredService<IDatasetImportTaskProvider>()
            .AuthorizeAsync(context.User, original.WorkspaceId, taskId, revision, contentHash, requireWrite, context.RequestAborted).ConfigureAwait(false);
        if (task is null) return false;
        var latest = await store.GetAsync(original.WorkspaceId).ConfigureAwait(false);
        var target = await store.GetTargetIdAsync().ConfigureAwait(false);
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        return actor is not null && latest is not null && latest.OwnerId == original.OwnerId &&
            latest.AgentActorId == original.AgentActorId && latest.TenantId == original.TenantId && target == original.TargetId &&
            AgentWorkspaceState.CanRead(latest, actor,
                context.RequestServices.GetService<Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext>()?.TenantId, target) &&
            await AgentCredentialAuthority.IsCurrentAsync(context.User, context.RequestServices, context.RequestAborted).ConfigureAwait(false);
    }
}
