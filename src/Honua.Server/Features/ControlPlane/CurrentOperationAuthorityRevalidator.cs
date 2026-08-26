// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Re-evaluates the retained proposer through the current durable grant store,
/// OAuth scope policy, exact resource binding, and edition ladder.
/// </summary>
internal sealed class CurrentOperationAuthorityRevalidator(
    IOperatorAuthorizationEvaluator authorization,
    IOperatorScopeAuthorizer scopeAuthorizer,
    IGuardrailLadder ladder,
    IPrincipalMembershipSource membershipSource,
    IAdminApiKeyStore apiKeyStore,
    IOptions<ApiKeyAuthenticationOptions> apiKeyOptions,
    IConnectionSecretResolver? secretResolver = null) : IOperationAuthorityRevalidator
{
    private const string OperatorBearerScheme = "OperatorBearer";
    private const string ServiceScheme = "Service";
    private const string TrustedServiceIssuer = "honua-server";
    private const string TrustedServiceActor = "ops-findings";
    private const string TrustedServiceTenant = "platform";
    private const string FrameworkAdminActor = "admin";
    private static readonly string[] FrameworkAdminRoles = ["admin"];
    private readonly ApiKeyAuthenticationOptions _apiKeyOptions =
        apiKeyOptions?.Value ?? throw new ArgumentNullException(nameof(apiKeyOptions));

    public async Task<OperationAuthorityRevalidationResult> RevalidateAsync(
        OperationProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var authority = proposal.Authority;
        if (authority?.ResourceType is not { } resourceType ||
            authority.Operation is not { } operation ||
            string.IsNullOrWhiteSpace(authority.ResourceId) ||
            string.IsNullOrWhiteSpace(authority.EffectiveTenant))
        {
            return OperationAuthorityRevalidationResult.Denied(
                "retained tenant, resource, and operation evidence is incomplete");
        }

        if (ladder.Resolve(proposal.Kind).Tier == GuardrailTier.Blocked)
        {
            return OperationAuthorityRevalidationResult.Denied("the current edition blocks this operation");
        }

        var currentAuthority = await ResolveCurrentAuthorityAsync(authority, cancellationToken)
            .ConfigureAwait(false);
        if (!currentAuthority.IsAllowed)
        {
            return OperationAuthorityRevalidationResult.Denied(currentAuthority.Reason!);
        }

        // This is the sole credential-less authority minted by the server itself. It has
        // no external account or API key to re-resolve, so retain the explicit in-process
        // trust binding instead of fabricating user roles. Edition and exact resource
        // evidence were still checked above.
        if (currentAuthority.IsTrustedService)
        {
            return OperationAuthorityRevalidationResult.Allowed();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, authority.Actor),
            new("iss", authority.Issuer),
        };
        claims.AddRange(authority.ScopeCeiling.Select(scope =>
            new Claim(OperatorScopeCatalog.ScopeClaimType, scope)));
        claims.AddRange(currentAuthority.Permissions.Select(permission =>
            new Claim("permission", permission)));
        claims.AddRange(currentAuthority.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            authority.Scheme,
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role));

        var grant = await authorization.EvaluateAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = resourceType,
                ResourceId = authority.ResourceId,
                Operation = operation,
            },
            cancellationToken).ConfigureAwait(false);
        if (!grant.IsAllowed)
        {
            return OperationAuthorityRevalidationResult.Denied(grant.FailureReason ?? "current grant denied");
        }

        var scope = scopeAuthorizer.Evaluate(principal, resourceType, operation);
        return scope.IsAllowed
            ? OperationAuthorityRevalidationResult.Allowed()
            : OperationAuthorityRevalidationResult.Denied(scope.Reason ?? "current OAuth scope denied");
    }

    private async Task<CurrentAuthority> ResolveCurrentAuthorityAsync(
        OperationAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        if (IsTrustedServiceAuthority(authority))
        {
            return CurrentAuthority.TrustedService();
        }

        if (string.Equals(
                authority.Scheme,
                AuthenticationExtensions.ApiKeyScheme,
                StringComparison.Ordinal))
        {
            if (!Guid.TryParse(authority.Actor, out var apiKeyId))
            {
                if (!IsFrameworkAdminAuthority(authority))
                {
                    return CurrentAuthority.Denied("the retained API-key identity cannot be resolved");
                }

                if (await IsFrameworkAdminCurrentlyAvailableAsync(cancellationToken).ConfigureAwait(false))
                {
                    return CurrentAuthority.Allowed(
                        IntersectCeiling(FrameworkAdminRoles, authority.RoleCeiling),
                        []);
                }

                return CurrentAuthority.Denied("the bootstrap admin credential is unavailable");
            }

            var apiKey = await apiKeyStore.GetAsync(apiKeyId, cancellationToken).ConfigureAwait(false);
            if (apiKey is null || apiKey.RevokedAt is not null ||
                apiKey.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            {
                return CurrentAuthority.Denied("the proposer API key is missing, revoked, or expired");
            }

            var permissions = IntersectCeiling(apiKey.Permissions, authority.PermissionCeiling);
            var currentRoles = ResolveApiKeyRoles(apiKey.Permissions);
            return CurrentAuthority.Allowed(
                IntersectCeiling(currentRoles, authority.RoleCeiling),
                permissions);
        }

        if (string.Equals(authority.Scheme, OperatorBearerScheme, StringComparison.OrdinalIgnoreCase) &&
            authority.MembershipIssuer is null)
        {
            return CurrentAuthority.Denied(
                "the retained operator-bearer membership issuer is unavailable; resubmission is required");
        }

        var membership = await membershipSource.ResolveMembershipAsync(
            authority.Actor,
            authority.MembershipIssuer ?? authority.Issuer,
            cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            return CurrentAuthority.Denied("the proposer's current membership cannot be resolved");
        }

        if (!membership.IsActive)
        {
            return CurrentAuthority.Denied("the proposer identity is no longer active");
        }

        return CurrentAuthority.Allowed(
            IntersectCeiling(membership.Roles, authority.RoleCeiling),
            authority.PermissionCeiling);
    }

    private static bool IsTrustedServiceAuthority(OperationAuthorityContext authority)
        => string.Equals(authority.Scheme, ServiceScheme, StringComparison.Ordinal) &&
            string.Equals(authority.Issuer, TrustedServiceIssuer, StringComparison.Ordinal) &&
            string.Equals(authority.Actor, TrustedServiceActor, StringComparison.Ordinal) &&
            string.Equals(authority.EffectiveTenant, TrustedServiceTenant, StringComparison.Ordinal);

    private static bool IsFrameworkAdminAuthority(OperationAuthorityContext authority)
        => string.Equals(authority.Issuer, AuthenticationExtensions.ApiKeyScheme, StringComparison.Ordinal) &&
            string.Equals(authority.Actor, FrameworkAdminActor, StringComparison.Ordinal) &&
            authority.ScopeGoverned is false &&
            authority.OAuthScopes.Count == 0 &&
            authority.ScopeCeiling.Count == 0 &&
            authority.Permissions.Count == 0 &&
            authority.PermissionCeiling.Count == 0 &&
            authority.Roles.Count == 1 &&
            authority.RoleCeiling.Count == 1 &&
            string.Equals(authority.Roles[0], FrameworkAdminRoles[0], StringComparison.OrdinalIgnoreCase) &&
            string.Equals(authority.RoleCeiling[0], FrameworkAdminRoles[0], StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsFrameworkAdminCurrentlyAvailableAsync(
        CancellationToken cancellationToken)
    {
        if (IsDevelopmentBypassActive(_apiKeyOptions))
        {
            return true;
        }

        try
        {
            var currentPassword = await AdminPasswordResolver.ResolveAsync(
                _apiKeyOptions,
                secretResolver,
                cancellationToken).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(currentPassword);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsDevelopmentBypassActive(ApiKeyAuthenticationOptions options)
        => options.IsTestMode &&
            string.Equals(options.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(options.DevAuthBypass, "true", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(options.DevAuthBypassAcknowledged, "true", StringComparison.OrdinalIgnoreCase);

    private static string[] ResolveApiKeyRoles(IReadOnlyList<string> permissions)
    {
        if (LayerScopedWriteKey.IsScopedWriteKey(permissions))
        {
            return [LayerScopedWriteKey.Role];
        }

        return LayerScopedWriteKey.ConfersFullAdmin(permissions)
            ? ["admin"]
            : [LayerScopedWriteKey.ScopedKeyRole];
    }

    private static string[] IntersectCeiling(
        IReadOnlyList<string> current,
        IReadOnlyList<string> ceiling)
    {
        var currentSet = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ceiling
            .Where(currentSet.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record CurrentAuthority(
        bool IsAllowed,
        bool IsTrustedService,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions,
        string? Reason)
    {
        public static CurrentAuthority Allowed(
            IReadOnlyList<string> roles,
            IReadOnlyList<string> permissions)
            => new(true, false, roles, permissions, null);

        public static CurrentAuthority TrustedService()
            => new(true, true, [], [], null);

        public static CurrentAuthority Denied(string reason)
            => new(false, false, [], [], reason);
    }
}
