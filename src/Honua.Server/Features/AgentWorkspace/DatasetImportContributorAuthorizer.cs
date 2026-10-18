// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed class DatasetImportContributorAuthorizer(IAgentWorkspaceStore store, IHttpContextAccessor accessor)
    : IDatasetImportContributorAuthorizer
{
    public async Task<DatasetImportContribution?> AuthorizeAsync(ClaimsPrincipal contributor, string workspaceId, string taskId,
        long revision, string contentHash, bool requireWrite, CancellationToken cancellationToken = default)
    {
        var transport = accessor.HttpContext;
        var actor = CanonicalSecurityActor.Resolve(contributor)?.ActorId;
        if (transport is null || contributor.Identity?.IsAuthenticated != true || AgentCredentialAuthority.IsAgent(contributor) ||
            actor is null || actor != CanonicalSecurityActor.Resolve(transport.User)?.ActorId ||
            !AgentWorkspaceState.IsIdentifier(workspaceId) || !AgentWorkspaceState.IsIdentifier(taskId) ||
            revision < 1 || !DatasetImportTaskState.Hash(contentHash)) return null;
        var services = transport.RequestServices;
        if (!await AgentCredentialAuthority.IsCurrentAsync(contributor, services, cancellationToken).ConfigureAwait(false)) return null;
        var tenant = services.GetService<ITenantContext>()?.TenantId;
        var target = await store.GetTargetIdAsync().ConfigureAwait(false);
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (document is null || !document.DatasetImportExecutionBindings.TryGetValue(taskId, out var binding) ||
            !document.DatasetImportTasks.TryGetValue(taskId, out var versions) ||
            versions.SingleOrDefault(task => task.Revision == revision) is not { } task ||
            !Bound(document, binding, task, actor, tenant, target, contentHash)) return null;
        var executor = await AgentCredentialAuthority.RevalidateAsync(binding.ExecutorAuthority, services, cancellationToken).ConfigureAwait(false);
        if (executor is null || CanonicalSecurityActor.Resolve(executor)?.ActorId != binding.ExecutorActorId) return null;
        // This principal is returned by the canonical credential authority, never
        // reconstructed from contributor claims or a caller-selected owner.
        var executorContext = new DefaultHttpContext { User = executor, RequestServices = services, RequestAborted = cancellationToken };
        var humanContext = new DefaultHttpContext { User = contributor, RequestServices = services, RequestAborted = cancellationToken };
        var destination = services.GetRequiredService<IDatasetImportDestinationAuthorizer>();
        if (!await destination.IsAuthorizedAsync(contributor, target, task.Destination, task.Options, requireWrite, cancellationToken).ConfigureAwait(false) ||
            !await destination.IsAuthorizedAsync(executor, target, task.Destination, task.Options, requireWrite, cancellationToken).ConfigureAwait(false) ||
            !await DatasetImportTaskProvider.SourceAuthorizedAsync(humanContext, document, task.Source, cancellationToken).ConfigureAwait(false) ||
            !await DatasetImportTaskProvider.SourceAuthorizedAsync(executorContext, document, task.Source, cancellationToken).ConfigureAwait(false)) return null;
        var turnAuthority = new AgentWorkspaceTurnAuthorizer(store, new PrivateAccessor { HttpContext = executorContext },
            services.GetRequiredService<IConfiguration>());
        var authorizedTurn = await turnAuthority.AuthorizeAsync(executor, workspaceId, binding.OriginatingTurnId, cancellationToken).ConfigureAwait(false);
        if (authorizedTurn is null || authorizedTurn.OwnerActorId != actor || authorizedTurn.TargetId != target || authorizedTurn.TenantId != tenant)
            return null;
        var latest = await store.GetAsync(workspaceId).ConfigureAwait(false);
        var currentTarget = await store.GetTargetIdAsync().ConfigureAwait(false);
        if (latest is null || !latest.DatasetImportExecutionBindings.TryGetValue(taskId, out var retained) ||
            retained.AuthorityRevision != binding.AuthorityRevision || retained.AuthorityFingerprint != binding.AuthorityFingerprint ||
            retained.PolicyContextFingerprint != binding.PolicyContextFingerprint ||
            !latest.DatasetImportTasks.TryGetValue(taskId, out var retainedVersions) ||
            retainedVersions.SingleOrDefault(item => item.Revision == revision) is not { } retainedTask ||
            retainedTask != task || !Bound(latest, retained, retainedTask, actor, tenant, currentTarget, contentHash) ||
            !await AgentCredentialAuthority.IsCurrentAsync(contributor, services, cancellationToken).ConfigureAwait(false) ||
            await AgentCredentialAuthority.RevalidateAsync(retained.ExecutorAuthority, services, cancellationToken).ConfigureAwait(false) is null)
            return null;
        return new(task, binding.ExecutorActorId, actor, actor, binding.OriginatingTurnId, binding.OriginalTurnHash,
            binding.AuthorityRevision, binding.AuthorityFingerprint, binding.PolicyContextFingerprint);
    }

    private static bool Bound(AgentWorkspaceDocument document, DatasetImportExecutionBinding binding, DatasetImportTask task,
        string human, string? tenant, string target, string hash)
        => document.OwnerId == human && binding.InitiatingHumanActorId == human &&
            document.AgentActorId == binding.ExecutorActorId && task.OwnerActorId == binding.ExecutorActorId &&
            task.TaskId == binding.TaskId && task.Revision == binding.TaskRevision && task.ContentHash == binding.TaskContentHash &&
            task.ContentHash == hash && DatasetImportTaskState.ContentHash(task) == hash && task.WorkspaceId == document.WorkspaceId &&
            task.TargetId == target && task.TenantId == tenant &&
            AgentWorkspaceState.CanRead(document, human, tenant, target) &&
            AgentWorkspaceState.CanRead(document, binding.ExecutorActorId, tenant, target) &&
            binding.ExecutorPolicyContext.PrincipalId == binding.ExecutorActorId &&
            binding.ExecutorPolicyContext.OwnerActorId == binding.ExecutorActorId && binding.ExecutorPolicyContext.TenantId == tenant &&
            binding.PolicyContextFingerprint == JobSecurityContextFingerprint.Compute(binding.ExecutorPolicyContext) &&
            document.Turns.TryGetValue(binding.OriginatingTurnId, out var turn) && turn.ActorId == human &&
            binding.OriginalTurnHash == Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));

    private sealed class PrivateAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
