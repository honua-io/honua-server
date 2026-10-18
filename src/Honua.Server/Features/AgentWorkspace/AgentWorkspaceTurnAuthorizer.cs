// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed class AgentWorkspaceTurnAuthorizer(
    IAgentWorkspaceStore store,
    IHttpContextAccessor accessor,
    IConfiguration configuration) : IAgentWorkspaceTurnAuthorizer
{
    public async Task<AgentWorkspaceTurnAuthorization?> AuthorizeAsync(
        ClaimsPrincipal principal, string workspaceId, string turnId, CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext;
        if (context is null || principal.Identity?.IsAuthenticated != true ||
            !AgentWorkspaceState.IsIdentifier(workspaceId) || !AgentWorkspaceState.IsIdentifier(turnId) ||
            !AgentCredentialAuthority.IsAgent(principal))
        {
            return null;
        }
        if (!await AgentCredentialAuthority.IsCurrentAsync(principal, context.RequestServices, cancellationToken).ConfigureAwait(false)) return null;
        var actor = CanonicalSecurityActor.Resolve(principal)?.ActorId;
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        var release = configuration["AgentWorkspace:ReleaseId"];
        if (actor is null || CanonicalSecurityActor.Resolve(context.User)?.ActorId != actor ||
            string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(release) ||
            !await OperationAdminAuthorization.IsAuthorizedAsync(context, principal,
                OperationSideEffectClass.ReadOnly, cancellationToken).ConfigureAwait(false)) return null;

        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (document is null || !AgentWorkspaceState.CanRead(document, actor, tenant,
                await store.GetTargetIdAsync().ConfigureAwait(false)) || !document.Turns.TryGetValue(turnId, out var turn)) return null;
        if (turn.Context.WorkspaceId != workspaceId || turn.Context.TargetId != document.TargetId) return null;
        foreach (var resource in turn.Context.SelectedResources)
        {
            if (!await AgentWorkspaceResourceAuthorization.CanReadAsync(context, resource, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
        if (!await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, turn.Context.Task, workspace: document).ConfigureAwait(false)) return null;
        if (!await LocalProcessWorkspaceAuthorization.CanReadTurnAsync(context, document, turn).ConfigureAwait(false)) return null;
        return new(workspaceId, turnId, turn.Context.TargetId, tenant, release, document.OwnerId);
    }
}

/// <summary>Rejects stale or foreign native-agent bindings before the canonical MCP dispatcher runs.</summary>
internal sealed class AgentWorkspaceTurnMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isMcp = path.StartsWithSegments("/mcp") || path.StartsWithSegments("/api/mcp");
        var hasWorkspace = context.Request.Headers.ContainsKey("X-Honua-Workspace");
        var hasTurn = context.Request.Headers.ContainsKey("X-Honua-Turn");
        if (isMcp && (hasWorkspace || hasTurn))
        {
            var authorizer = context.RequestServices.GetRequiredService<IAgentWorkspaceTurnAuthorizer>();
            AgentWorkspaceTurnAuthorization? binding;
            try
            {
                binding = await authorizer.AuthorizeAsync(context.User,
                    context.Request.Headers["X-Honua-Workspace"].ToString(),
                    context.Request.Headers["X-Honua-Turn"].ToString(), context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is StackExchange.Redis.RedisException or AgentWorkspaceUnavailableException)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (binding is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Items[typeof(AgentWorkspaceTurnAuthorization)] = binding;
        }
        await next(context).ConfigureAwait(false);
    }
}
