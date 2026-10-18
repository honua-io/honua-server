// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal static class LocalProcessWorkspaceAuthorization
{
    internal static LocalProcessWorkspaceAccess? TrustedAccess(HttpContext context, AgentWorkspaceDocument workspace)
    {
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        var delegated = context.User.FindFirst("api_key_id") is not null ||
            context.User.Identity?.AuthenticationType == AuthenticationExtensions.ApiKeyScheme;
        if (context.User.Identity?.IsAuthenticated != true || actor is null ||
            !AgentWorkspaceState.CanRead(workspace, actor, tenant, workspace.TargetId) ||
            (delegated ? !context.User.HasClaim("permission", "agent:workspace") : actor != workspace.OwnerId)) return null;
        return new(actor, workspace.OwnerId, workspace.WorkspaceId, workspace.TargetId, tenant, delegated);
    }

    internal static async Task<bool> CanReadTaskAsync(HttpContext context, AgentWorkspaceDocument? workspace,
        AgentTaskReference task, bool requireGeneration)
    {
        if (workspace is null || TrustedAccess(context, workspace) is not { } access) return false;
        var gateway = context.RequestServices.GetService<ILocalProcessWorkspaceGateway>();
        if (gateway?.IsConfigured != true) return false;
        try
        {
            var identity = await gateway.GetIdentityAsync(access, context.RequestAborted).ConfigureAwait(false);
            if (identity.ProjectId != task.Id || !BindingMatches(identity.Binding, access)) return false;
            if (requireGeneration && (identity.Revision != task.Revision || identity.SourceDigest != task.ContentHash))
                throw new AgentWorkspaceConflictException("The local process source changed. Review its current revision before sending.");
            return true;
        }
        catch (LocalProcessWorkspaceException exception) when (exception.StatusCode is 403 or 404) { return false; }
    }

    internal static async Task<bool> CanReadTurnAsync(HttpContext context, AgentWorkspaceDocument workspace, AgentWorkspaceTurn turn)
    {
        if (turn.Context.Task is not { Kind: "local-process-workspace" } task) return turn.LocalProcessSnapshot is null;
        if (TrustedAccess(context, workspace) is not { } access || turn.LocalProcessSnapshot is not { } snapshot ||
            snapshot.ProjectId != task.Id || snapshot.Revision != task.Revision || snapshot.SnapshotDigest != task.ContentHash) return false;
        var gateway = context.RequestServices.GetService<ILocalProcessWorkspaceGateway>();
        if (gateway?.IsConfigured != true) return false;
        try
        {
            var retained = await gateway.GetSnapshotAsync(access, snapshot.SnapshotId, context.RequestAborted).ConfigureAwait(false);
            return retained == snapshot;
        }
        catch (LocalProcessWorkspaceException exception) when (exception.StatusCode is 403 or 404) { return false; }
    }

    internal static async Task<LocalProcessWorkspaceAccess> AuthorizeAsync(HttpContext context, string workspaceId,
        string? turnId, bool readOnly)
    {
        if (!AgentWorkspaceState.IsIdentifier(workspaceId) ||
            !await OperationAdminAuthorization.IsAuthorizedAsync(context, context.User,
                readOnly ? OperationSideEffectClass.ReadOnly : OperationSideEffectClass.MutatesMetadata, context.RequestAborted).ConfigureAwait(false))
            throw new LocalProcessWorkspaceException(403, "Local process access denied.");
        var store = context.RequestServices.GetRequiredService<IAgentWorkspaceStore>();
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (document is null || document.TargetId != await store.GetTargetIdAsync().ConfigureAwait(false) ||
            TrustedAccess(context, document) is not { } access)
            throw new LocalProcessWorkspaceException(404, "Local process workspace not found.");
        var headerWorkspace = context.Request.Headers["X-Honua-Workspace"].ToString();
        var headerTurn = context.Request.Headers["X-Honua-Turn"].ToString();
        if (headerWorkspace.Length > 0 && headerWorkspace != workspaceId || headerTurn.Length > 0 && headerTurn != turnId)
            throw new LocalProcessWorkspaceException(403, "The request does not match the active workspace turn.");
        if (access.Delegated)
        {
            if (!AgentWorkspaceState.IsIdentifier(turnId) ||
                await context.RequestServices.GetRequiredService<IAgentWorkspaceTurnAuthorizer>()
                    .AuthorizeAsync(context.User, workspaceId, turnId!, context.RequestAborted).ConfigureAwait(false) is null ||
                !document.Turns.TryGetValue(turnId!, out var turn) || turn.Context.Task?.Kind != "local-process-workspace" ||
                !await CanReadTurnAsync(context, document, turn).ConfigureAwait(false))
                throw new LocalProcessWorkspaceException(403, "A current authorized local-process turn is required.");
        }
        return access;
    }

    private static bool BindingMatches(LocalProcessProjectBinding? binding, LocalProcessWorkspaceAccess access)
        => binding is not null && binding.OwnerActorId == access.OwnerActorId && binding.TenantId == access.TenantId &&
            binding.EnvironmentId == access.TargetId && binding.WorkspaceId == access.WorkspaceId;
}
