using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Evaluates operator-scoped resource authorization using convention mapping from
/// the existing <see cref="IRoleStore"/> permission grants.
/// </summary>
internal sealed class OperatorAuthorizationEvaluator(
    IRoleStore roleStore,
    IOptions<RbacOptions> rbacOptions,
    ILogger<OperatorAuthorizationEvaluator> logger) : IOperatorAuthorizationEvaluator
{
    public async Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, OperatorAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is
            {
                ResourceType: OperatorResourceType.Workspace,
                WorkspaceVisibility: WorkspaceVisibility.Public,
                Operation: OperatorOperation.Read or OperatorOperation.Discover
            })
        {
            OperatorAuthorizationLog.PublicWorkspaceAllowed(logger, request.Operation, request.ResourceId);
            return AccessDecision.Allowed();
        }

        if (principal.Identity is not { IsAuthenticated: true })
        {
            OperatorAuthorizationLog.AuthenticationRequired(logger, request.ResourceType, request.Operation);
            return AccessDecision.RequiresAuth("Authentication is required for operator resources.");
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        var roleClaimType = rbacOptions.Value.EffectiveRoleClaimType;
        var checkStandardRoleClaim = !string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase);

        bool isAdmin = false;
        List<string>? roleNames = null;

        foreach (var claim in principal.Claims)
        {
            var isRoleClaim = string.Equals(claim.Type, roleClaimType, StringComparison.OrdinalIgnoreCase)
                || (checkStandardRoleClaim && string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase));

            if (!isRoleClaim || string.IsNullOrWhiteSpace(claim.Value))
                continue;

            if (string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase))
            {
                isAdmin = true;
                break;
            }

            roleNames ??= [];
            roleNames.Add(claim.Value);
        }

        if (isAdmin)
        {
            OperatorAuthorizationLog.AdminBypassed(logger, userId, request.ResourceType, request.Operation);
            return AccessDecision.Allowed();
        }

        if (request.ResourceType == OperatorResourceType.Workspace)
        {
            switch (request.WorkspaceVisibility)
            {
                case null:
                    OperatorAuthorizationLog.WorkspaceMissingVisibility(logger, userId);
                    return AccessDecision.Forbidden("Workspace access denied: visibility context is required.");

                case WorkspaceVisibility.Public
                    when request.Operation is not (OperatorOperation.Read or OperatorOperation.Discover):
                    OperatorAuthorizationLog.PublicWorkspaceMutationDenied(logger, userId, request.Operation);
                    return AccessDecision.Forbidden("Public workspaces are read-only.");

                case WorkspaceVisibility.Personal:
                    if (request.WorkspaceOwnerId is null)
                    {
                        OperatorAuthorizationLog.PersonalWorkspaceMissingOwner(logger, userId);
                        return AccessDecision.Forbidden("Personal workspace access denied: owner context is required.");
                    }

                    if (!string.Equals(userId, request.WorkspaceOwnerId, StringComparison.Ordinal))
                    {
                        OperatorAuthorizationLog.WorkspaceOwnershipDenied(logger, userId, request.WorkspaceOwnerId);
                        return AccessDecision.Forbidden("Personal workspace access denied: principal is not the workspace owner.");
                    }

                    break;

                case WorkspaceVisibility.Shared:
                    if (request.WorkspaceScopeId is null)
                    {
                        OperatorAuthorizationLog.SharedWorkspaceMissingScope(logger, userId);
                        return AccessDecision.Forbidden("Shared workspace access denied: scope context is required.");
                    }

                    if (!HasScopeClaim(principal, rbacOptions.Value.WorkspaceScopeClaimType, request.WorkspaceScopeId))
                    {
                        OperatorAuthorizationLog.SharedWorkspaceScopeDenied(logger, userId, request.WorkspaceScopeId);
                        return AccessDecision.Forbidden("Shared workspace access denied: principal is not in the workspace scope.");
                    }

                    break;
            }
        }

        if (roleNames is not { Count: > 0 })
        {
            OperatorAuthorizationLog.PermissionDenied(logger, userId, request.ResourceType, request.Operation, request.ResourceId);
            return AccessDecision.Forbidden("No operator-eligible roles assigned.");
        }

        // IRoleStore is singleton (InMemoryRoleStore) and returns completed tasks.
        // When a persistent store is introduced (#498), a per-request caching layer
        // should resolve permissions once and pass them through scoped context.
        var effective = await roleStore.GetEffectivePermissionsAsync(
            userId ?? string.Empty, roleNames, cancellationToken).ConfigureAwait(false);

        var permissions = effective.Permissions;
        for (var i = 0; i < permissions.Count; i++)
        {
            if (MatchesOperatorGrant(permissions[i], request))
            {
                OperatorAuthorizationLog.PermissionGranted(
                    logger, userId, request.ResourceType, request.Operation, request.ResourceId);
                return AccessDecision.Allowed();
            }
        }

        OperatorAuthorizationLog.PermissionDenied(
            logger, userId, request.ResourceType, request.Operation, request.ResourceId);
        return AccessDecision.Forbidden(
            $"No matching operator permission for {request.ResourceType}.{request.Operation}.");
    }

    private bool MatchesOperatorGrant(PermissionGrant grant, OperatorAuthorizationRequest request)
    {
        if (!IsResourceTypeMatch(grant.Service, request.ResourceType))
            return false;

        if (!IsOperationMatch(grant.Operation, request.Operation))
            return false;

        if (grant.Layer.Length == 1 && grant.Layer[0] == '*')
            return true;

        if (request.ResourceId is null)
            return false;

        return string.Equals(grant.Layer, request.ResourceId, StringComparison.Ordinal);
    }

    private bool IsResourceTypeMatch(string service, OperatorResourceType requested)
    {
        if (service.Length == 1 && service[0] == '*')
            return true;

        if (!Enum.TryParse<OperatorResourceType>(service, ignoreCase: true, out var parsed))
        {
            OperatorAuthorizationLog.UnrecognizedResourceType(logger, service);
            return false;
        }

        return parsed == requested;
    }

    private static bool IsOperationMatch(string operation, OperatorOperation requested)
    {
        if (operation.Length == 1 && operation[0] == '*')
            return true;

        return Enum.TryParse<OperatorOperation>(operation, ignoreCase: true, out var parsed)
            && parsed == requested;
    }

    private static bool HasScopeClaim(ClaimsPrincipal principal, string claimType, string scopeId)
    {
        foreach (var claim in principal.Claims)
        {
            if (string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(claim.Value, scopeId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
