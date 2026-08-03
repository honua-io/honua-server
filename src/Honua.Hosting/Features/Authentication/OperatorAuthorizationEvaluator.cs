using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Evaluates operator-scoped resource authorization using convention mapping from
/// the existing <see cref="IRoleStore"/> permission grants.
/// </summary>
/// <remarks>
/// Registered as a singleton. <see cref="IRoleStore"/> is resolved per call via
/// <see cref="IServiceScopeFactory"/> so that a scoped store implementation
/// (e.g. PostgresRoleStore, which captures a scoped
/// <c>IDatabaseConnectionProvider</c>) works correctly once durable RBAC lands.
/// </remarks>
internal sealed class OperatorAuthorizationEvaluator(
    IServiceScopeFactory scopeFactory,
    IOptions<RbacOptions> rbacOptions,
    ILogger<OperatorAuthorizationEvaluator> logger,
    IServiceProvider? serviceProvider = null) : IOperatorAuthorizationEvaluator
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

        var userId = ResolveGrantSubjectId(principal);
        var roleNames = RbacRoleClaims.Enumerate(principal, rbacOptions.Value, serviceProvider);
        var isAdmin = roleNames.Any(role =>
            string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));

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

        if (roleNames.Count == 0)
        {
            OperatorAuthorizationLog.PermissionDenied(logger, userId, request.ResourceType, request.Operation, request.ResourceId);
            return AccessDecision.Forbidden("No operator-eligible roles assigned.");
        }

        // This evaluator is a singleton, but IRoleStore may be a scoped durable provider
        // (PostgresRoleStore depends on the scoped connection provider). Resolve it within a
        // fresh scope per evaluation so the singleton never captures a scoped dependency
        // (#1575). EffectivePermissions is fully materialised before the scope is disposed.
        EffectivePermissions effective;
        using (var scope = scopeFactory.CreateScope())
        {
            var roleStore = scope.ServiceProvider.GetRequiredService<IRoleStore>();
            effective = await roleStore.GetEffectivePermissionsAsync(
                userId ?? string.Empty, roleNames, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Resolves the subject id used for role-store grant lookup and workspace ownership
    /// comparison: <see cref="ClaimTypes.NameIdentifier"/>, then <c>sub</c>, then
    /// <c>api_key_id</c>, then <c>api_key_name</c>.
    /// </summary>
    /// <remarks>
    /// The API-key fallbacks mirror <c>StudioAuthorizationService.ResolveCallerId</c> so that
    /// the id a caller owns content under is the same id an operator provisions grants
    /// against. API-key principals carry neither <see cref="ClaimTypes.NameIdentifier"/> nor
    /// <c>sub</c> (ApiKeyAuthenticationHandler stamps <c>api_key_id</c>/<c>api_key_name</c>
    /// instead), so without these fallbacks every API-key caller collapsed onto the same
    /// empty subject id and per-key grants could never match (#3023 review).
    /// </remarks>
    private static string? ResolveGrantSubjectId(ClaimsPrincipal principal)
    {
        var candidate = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirstValue("api_key_id");
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirstValue("api_key_name");
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
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
        return principal.Claims.Any(claim =>
            string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Value, scopeId, StringComparison.Ordinal));
    }
}
