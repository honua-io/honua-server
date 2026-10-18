// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;

namespace Honua.Server.Features.Styling;

internal sealed record SharedStyleTurnBinding(AgentWorkspaceTurnAuthorization Binding, AgentTaskReference Task);

internal static class SharedStyleWorkspaceAuthorization
{
    internal static async Task<SharedStyleTurnBinding> AuthorizeAsync(HttpContext context, ClaimsPrincipal principal,
        string workspaceId, string turnId, CancellationToken cancellationToken)
    {
        var actor = CanonicalSecurityActor.Resolve(principal)?.ActorId;
        if (actor is null || actor != CanonicalSecurityActor.Resolve(context.User)?.ActorId ||
            !context.User.HasClaim("permission", "agent:workspace") || context.User.FindFirst("api_key_id") is null ||
            !AgentWorkspaceState.IsIdentifier(workspaceId) || !AgentWorkspaceState.IsIdentifier(turnId)) Deny();
        var headers = context.Request.Headers;
        if (headers["X-Honua-Workspace"].Count > 1 || headers["X-Honua-Turn"].Count > 1 ||
            (headers.ContainsKey("X-Honua-Workspace") || headers.ContainsKey("X-Honua-Turn")) &&
            (headers["X-Honua-Workspace"] != workspaceId || headers["X-Honua-Turn"] != turnId)) Deny();
        await SharedStyleAuthority.RequireCurrentKeyAsync(context, cancellationToken).ConfigureAwait(false);
        var store = context.RequestServices.GetRequiredService<IAgentWorkspaceStore>();
        var authorizer = new AgentWorkspaceTurnAuthorizer(store, new PrivateAccessor { HttpContext = context },
            context.RequestServices.GetRequiredService<IConfiguration>());
        var binding = await authorizer.AuthorizeAsync(context.User, workspaceId, turnId, cancellationToken).ConfigureAwait(false);
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (binding is null || document is null || binding.OwnerActorId != document.OwnerId ||
            binding.TenantId != context.RequestServices.GetRequiredService<ITenantContext>().TenantId ||
            !AgentWorkspaceState.CanRead(document, actor!, binding.TenantId, binding.TargetId)) Deny();
        if (!document!.Turns.TryGetValue(turnId, out var turn)) Deny();
        var task = turn?.Context.Task;
        if (task is not { Kind: "shared-style" }) Deny();
        return new(binding!, task!);
    }

    // Framework HttpContextAccessor uses shared AsyncLocal state, even across instances.
    // A private accessor prevents replay from replacing the actual human approver.
    internal sealed class PrivateAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
}
