// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>Resolves package ownership from actual authentication and fresh workspace authorization.</summary>
internal sealed class WorkflowPackageAuthorization(
    IHttpContextAccessor accessor,
    ITenantContext tenantContext,
    IAgentWorkspaceTurnAuthorizer? turns = null)
{
    public async Task<WorkflowPackageAccess> ResolveAsync(
        ClaimsPrincipal? principal, CancellationToken cancellationToken,
        string? workspaceId = null, string? turnId = null, bool write = false)
    {
        var context = accessor.HttpContext;
        principal ??= context?.User;
        var actor = CanonicalSecurityActor.Resolve(principal);
        if (principal is null || principal.Identity?.IsAuthenticated != true || actor is null)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        if (context is not null && CanonicalSecurityActor.Resolve(context.User)?.ActorId != actor.ActorId)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        // A same-actor copy cannot remove the delegated-agent marker or add grants.
        if (context is not null) principal = context.User;
        if (context is null || !await OperationAdminAuthorization.IsAuthorizedAsync(context, principal,
                write ? OperationSideEffectClass.MutatesMetadata : OperationSideEffectClass.ReadOnly,
                cancellationToken).ConfigureAwait(false))
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var agent = principal.HasClaim("permission", "agent:workspace");
        if (agent && (principal.FindFirst("api_key_id") is null || !actor.IsDurablyRevalidatable ||
                      string.IsNullOrWhiteSpace(tenantContext.TenantId)))
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);

        var headerWorkspace = context.Request.Headers["X-Honua-Workspace"];
        var headerTurn = context.Request.Headers["X-Honua-Turn"];
        if (headerWorkspace.Count > 1 || headerTurn.Count > 1)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var hasHeaders = context.Request.Headers.ContainsKey("X-Honua-Workspace") ||
                         context.Request.Headers.ContainsKey("X-Honua-Turn");
        if (hasHeaders)
        {
            if ((workspaceId is not null && workspaceId != headerWorkspace.ToString()) ||
                (turnId is not null && turnId != headerTurn.ToString()))
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            workspaceId = headerWorkspace.ToString();
            turnId = headerTurn.ToString();
        }
        string? delegatedOwner = null;
        Honua.Core.Features.AgentWorkspace.Domain.AgentWorkspaceTurnAuthorization? trustedBinding = null;
        if (workspaceId is not null || turnId is not null)
        {
            if (!agent || turns is null || !Identifier(workspaceId) || !Identifier(turnId))
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            var binding = await turns.AuthorizeAsync(principal, workspaceId!, turnId!, cancellationToken).ConfigureAwait(false);
            if (binding is null || binding.WorkspaceId != workspaceId || binding.TurnId != turnId ||
                binding.TenantId != tenantContext.TenantId || string.IsNullOrWhiteSpace(binding.OwnerActorId))
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            delegatedOwner = binding.OwnerActorId;
            trustedBinding = binding;
        }
        return new(actor.ActorId, tenantContext.TenantId, actor.IsDurablyRevalidatable, agent, delegatedOwner, context, trustedBinding);
    }

    private static bool Identifier(string? value)
        => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

/// <summary>Trusted, request-local ownership evidence; never bound from client JSON.</summary>
internal sealed record WorkflowPackageAccess(
    string ActorId, string? TenantId, bool DurableActor, bool WorkspaceAgent, string? DelegatedOwnerActorId, HttpContext? Context,
    Honua.Core.Features.AgentWorkspace.Domain.AgentWorkspaceTurnAuthorization? TurnBinding = null)
{
    public bool CanAccess(WorkflowPackageOwnership? owner)
    {
        if (WorkspaceAgent)
            return owner is not null && owner.TenantId == TenantId &&
                   (owner.OwnerActorId == ActorId || owner.OwnerActorId == DelegatedOwnerActorId);
        // Keep unknown legacy ownership available to existing human administrator flows.
        return owner is null || (Context is not null
            ? OperationTenantAuthorization.CanAccess(Context, owner.TenantId)
            : owner.TenantId == TenantId);
    }

    public WorkflowPackageOwnership? Ownership => DurableActor ? new()
    {
        OwnerActorId = DelegatedOwnerActorId ?? ActorId,
        TenantId = TenantId,
    } : null;
}
