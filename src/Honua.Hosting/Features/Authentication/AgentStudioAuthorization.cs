// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>Exact-item agent authority shared by Studio HTTP and MCP adapters.</summary>
internal static class AgentStudioAuthorization
{
    internal const string Compose = "studio-compose";
    internal const string ProposePublication = "studio-propose-publication";
    internal const string DeniedCode = "studio_authorization/agent_item_permission_required";

    internal static bool HasItemPermission(ClaimsPrincipal principal, Guid itemId, string action)
        => itemId != Guid.Empty && principal.HasClaim("permission", $"agent:{action}:{itemId:D}");

    internal static async Task<AgentWorkspaceTurnAuthorization?> BindAsync(
        HttpContext context, ClaimsPrincipal principal, CancellationToken cancellationToken,
        IAgentWorkspaceTurnAuthorizer? turnAuthorizer = null)
    {
        if (!AgentCredentialAuthority.IsAgent(principal) ||
            !await AgentCredentialAuthority.IsCurrentAsync(principal, context.RequestServices, cancellationToken).ConfigureAwait(false))
            return null;
        var workspace = context.Request.Headers["X-Honua-Workspace"];
        var turn = context.Request.Headers["X-Honua-Turn"];
        if (workspace.Count != 1 || turn.Count != 1 || string.IsNullOrWhiteSpace(workspace[0]) || string.IsNullOrWhiteSpace(turn[0]))
            return null;
        var authorizer = turnAuthorizer ?? context.RequestServices.GetService<IAgentWorkspaceTurnAuthorizer>();
        return authorizer is null ? null : await authorizer.AuthorizeAsync(
            principal, workspace[0]!, turn[0]!, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<StudioAuthorizationDecision> AuthorizeAsync(
        HttpContext context, ClaimsPrincipal principal, StudioAuthorizationOperation operation,
        string? resourceTenantId, string resourceType, string? resourceId, CancellationToken cancellationToken,
        IAgentWorkspaceTurnAuthorizer? turnAuthorizer = null)
    {
        if (context.RequestServices.GetService<IStudioAuthorizationService>()?.IsEndUserAuthorizationEnabled != true)
            return Deny();
        if (operation is StudioAuthorizationOperation.ListOwn or StudioAuthorizationOperation.Generate
            or StudioAuthorizationOperation.Rollback or StudioAuthorizationOperation.DeleteDraft
            or StudioAuthorizationOperation.ReopenVersion || !Guid.TryParse(resourceId, out var id))
            return Deny();

        var itemId = id;
        if (resourceType == "studio-package-draft" && operation != StudioAuthorizationOperation.CreateDraft)
        {
            var lifecycle = context.RequestServices.GetService<IStudioPackageLifecycleService>();
            var draft = lifecycle is null ? null : await lifecycle.GetDraftAsync(id, cancellationToken).ConfigureAwait(false);
            if (draft is null) return Deny();
            itemId = draft.ItemId;
            resourceTenantId = draft.TenantId;
        }
        var action = operation == StudioAuthorizationOperation.PublishRequest ? ProposePublication : Compose;
        if (!HasItemPermission(principal, itemId, action)) return Deny();

        var services = context.RequestServices;
        var tenant = services.GetService<ITenantContext>();
        var isolation = services.GetService<IOptionsMonitor<TenantIsolationOptions>>()?.CurrentValue ?? new();
        if (tenant is null || !TenantOwnership.CanAccess(resourceTenantId, tenant.TenantId, isolation)) return Deny();
        var scope = services.GetRequiredService<IOperatorScopeAuthorizer>().Evaluate(principal,
            OperatorResourceType.StudioDraft, operation switch
            {
                StudioAuthorizationOperation.PublishRequest => OperatorOperation.Publish,
                StudioAuthorizationOperation.ReadDraft or StudioAuthorizationOperation.ReadContentItem
                    or StudioAuthorizationOperation.ValidateDraft => OperatorOperation.Read,
                StudioAuthorizationOperation.UpdateDraft => OperatorOperation.Update,
                _ => OperatorOperation.Create,
            });
        if (!scope.IsAllowed) return StudioAuthorizationDecision.Deny(
            "studio_authorization/insufficient_scope", scope.Reason ?? "The token scope does not permit this Studio operation.");
        return await BindAsync(context, principal, cancellationToken, turnAuthorizer).ConfigureAwait(false) is null
            ? Deny() : StudioAuthorizationDecision.Allow(operation == StudioAuthorizationOperation.PublishRequest);
    }

    private static StudioAuthorizationDecision Deny() => StudioAuthorizationDecision.Deny(
        DeniedCode, "A live workspace turn and an exact Studio item permission are required.");
}
