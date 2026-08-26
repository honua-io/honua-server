// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// The authenticated authority snapshot captured for a mutating operation.
/// This is proposer authority: an approver can resolve a proposal, but never
/// replaces this snapshot during execution.
/// </summary>
public sealed record OperationAuthorityContext
{
    /// <summary>
    /// Private claim carrying the upstream identity-provider issuer used for live membership
    /// lookup after Honua exchanges an admin session for an operator bearer.
    /// </summary>
    public const string MembershipIssuerClaimType = JobSecurityContextClaimTypes.MembershipIssuer;

    private const string OperatorBearerScheme = "OperatorBearer";
    private const string ApiKeyIdClaim = "api_key_id";
    private const string ApiKeyNameClaim = "api_key_name";
    private const string ApiKeyPermissionClaim = "permission";

    /// <summary>Canonical value for a deliberately tenant-less operation context.</summary>
    public const string Tenantless = "$tenantless";

    /// <summary>Canonical token issuer or API-key provider.</summary>
    public required string Issuer { get; init; }

    /// <summary>
    /// Upstream identity-provider issuer used only to re-query managed membership. This differs
    /// from <see cref="Issuer"/> when a server-minted operator bearer is the transport credential.
    /// </summary>
    public string? MembershipIssuer { get; init; }

    /// <summary>Canonical authenticated actor identifier.</summary>
    public required string Actor { get; init; }

    /// <summary>Authentication scheme used to establish the actor.</summary>
    public required string Scheme { get; init; }

    /// <summary>Tenant selected after authentication and authorization.</summary>
    public required string EffectiveTenant { get; init; }

    /// <summary>OAuth scopes present on the authenticated request.</summary>
    public IReadOnlyList<string> OAuthScopes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The maximum permission set available to this operation. It must be a subset
    /// of <see cref="OAuthScopes"/> so replay can only narrow authority.
    /// </summary>
    public IReadOnlyList<string> ScopeCeiling { get; init; } = Array.Empty<string>();

    /// <summary>API-key/RBAC permission grants present on the authenticated request.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>Role evidence present when the proposal was submitted.</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The maximum permission-grant set available to this operation. It must be a subset of
    /// <see cref="Permissions"/> so approved replay cannot gain an API-key permission.
    /// </summary>
    public IReadOnlyList<string> PermissionCeiling { get; init; } = Array.Empty<string>();

    /// <summary>Maximum retained role set used only to re-query current grants at replay.</summary>
    public IReadOnlyList<string> RoleCeiling { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether this authority was established from an OAuth bearer token. A missing value marks
    /// a legacy record whose authentication provenance cannot be established and must fail closed.
    /// </summary>
    public bool? ScopeGoverned { get; init; }

    /// <summary>Canonical resource family bound to the authority, when applicable.</summary>
    public OperatorResourceType? ResourceType { get; init; }

    /// <summary>Canonical operation bound to the authority, when applicable.</summary>
    public OperatorOperation? Operation { get; init; }

    /// <summary>Exact resource identifier bound to the authority, when applicable.</summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Captures a bounded authority snapshot from an already-authenticated principal and the
    /// effective tenant selected by request middleware. Only identity, scope, and permission claims are
    /// retained; credentials and token material never enter the durable proposal.
    /// </summary>
    public static OperationAuthorityContext Capture(
        ClaimsPrincipal principal,
        string effectiveTenant)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var identity = principal.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated)
            ?? throw new InvalidOperationException(
                "An authenticated principal is required to capture operation authority.");
        var scheme = identity.AuthenticationType;
        var actor = ResolveActor(identity);
        var issuer = identity.FindFirst("iss")?.Value ?? scheme;
        var membershipIssuer = string.Equals(scheme, OperatorBearerScheme, StringComparison.OrdinalIgnoreCase)
            ? identity.FindFirst(MembershipIssuerClaimType)?.Value
            : null;
        var scopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();
        var permissions = identity.FindAll(ApiKeyPermissionClaim)
            .Select(claim => claim.Value.Trim())
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();
        var roles = identity.FindAll(identity.RoleClaimType)
            .Select(claim => claim.Value.Trim())
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(role => role, StringComparer.Ordinal)
            .ToArray();

        return CreateValidated(
            issuer,
            membershipIssuer,
            actor,
            scheme,
            effectiveTenant,
            scopes,
            permissions,
            roles,
            OperatorScopeCatalog.IsScopeGoverned(principal));
    }

    /// <summary>
    /// Captures authority using the effective tenant already resolved for the current request.
    /// Mutation proposals fail closed when no tenant was selected.
    /// </summary>
    public static OperationAuthorityContext Capture(
        ClaimsPrincipal principal,
        ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        if (!tenantContext.RequireTenantId(out var tenantId, out var reason))
        {
            throw new InvalidOperationException(
                $"An effective tenant is required to capture operation authority ({reason}).");
        }

        return Capture(principal, tenantId);
    }

    /// <summary>
    /// Captures authority using the configured tenant-resolution mode. Deployments that
    /// deliberately disable multi-tenancy retain an explicit tenant-less authority marker;
    /// tenant-aware deployments continue to fail closed when request middleware did not
    /// resolve a tenant.
    /// </summary>
    public static OperationAuthorityContext Capture(
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        bool multiTenancyEnabled)
        => multiTenancyEnabled
            ? Capture(principal, tenantContext)
            : Capture(principal, Tenantless);

    /// <summary>
    /// Creates an explicit authority snapshot for a trusted in-process service actor. This is
    /// used only where there is deliberately no ambient request principal (for example the
    /// deterministic ops-findings autonomy loop).
    /// </summary>
    public static OperationAuthorityContext CaptureService(
        string issuer,
        string actor,
        string effectiveTenant)
        => CreateValidated(
            issuer,
            membershipIssuer: null,
            actor,
            "Service",
            effectiveTenant,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            scopeGoverned: false);

    /// <summary>
    /// Resolves the stable actor identifier used by both proposal creation and approval
    /// separation-of-duties checks. Display names are only a final compatibility fallback.
    /// </summary>
    public static string? ResolveActor(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identity = principal.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated);
        return identity is null ? null : ResolveActor(identity);
    }
    /// <summary>
    /// Validates the bounded, non-secret authority lineage before it is persisted.
    /// </summary>
    public bool TryValidate(out string? error)
    {
        if (!IsBounded(Issuer, 512) || !IsBounded(Actor, 256) ||
            !IsBounded(Scheme, 64) || !IsBounded(EffectiveTenant, 256))
        {
            error = "Operation authority identifiers are missing or exceed their bounds.";
            return false;
        }

        if (MembershipIssuer is not null &&
            (!IsBounded(MembershipIssuer, 512) ||
             !string.Equals(Scheme, OperatorBearerScheme, StringComparison.OrdinalIgnoreCase)))
        {
            error = "Operation membership issuer is invalid for the authentication scheme.";
            return false;
        }

        if (ResourceId is not null && !IsBounded(ResourceId, 512))
        {
            error = "Operation authority resource identifier exceeds its bounds.";
            return false;
        }

        if (OAuthScopes.Count > 128 || ScopeCeiling.Count > 128 ||
            OAuthScopes.Any(scope => !IsBounded(scope, 256)) ||
            ScopeCeiling.Any(scope => !IsBounded(scope, 256)))
        {
            error = "Operation authority scopes are missing or exceed their bounds.";
            return false;
        }

        if (Permissions.Count > 128 || PermissionCeiling.Count > 128 ||
            Permissions.Any(permission => !IsBounded(permission, 256)) ||
            PermissionCeiling.Any(permission => !IsBounded(permission, 256)))
        {
            error = "Operation authority permissions are missing or exceed their bounds.";
            return false;
        }

        if (Roles.Count > 128 || RoleCeiling.Count > 128 ||
            Roles.Any(role => !IsBounded(role, 256)) ||
            RoleCeiling.Any(role => !IsBounded(role, 256)))
        {
            error = "Operation authority roles are missing or exceed their bounds.";
            return false;
        }

        var granted = OAuthScopes.ToHashSet(StringComparer.Ordinal);
        if (ScopeCeiling.Any(scope => !granted.Contains(scope)))
        {
            error = "Operation scope ceiling exceeds the authenticated scope set.";
            return false;
        }

        var permissions = Permissions.ToHashSet(StringComparer.Ordinal);
        if (PermissionCeiling.Any(permission => !permissions.Contains(permission)))
        {
            error = "Operation permission ceiling exceeds the authenticated permission set.";
            return false;
        }


        var roles = Roles.ToHashSet(StringComparer.Ordinal);
        if (RoleCeiling.Any(role => !roles.Contains(role)))
        {
            error = "Operation role ceiling exceeds the authenticated role set.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Returns whether the persisted scope ceiling still permits the canonical operation.
    /// Non-OAuth authority remains governed by the normal grant/RBAC decision.
    /// </summary>
    public bool PermitsBoundOperation()
    {
        if (ScopeGoverned is null)
        {
            return false;
        }

        return !IsScopeGovernedForReplay()
            || (ResourceType is not null
                && Operation is { } operation
                && OperatorScopeCatalog.PermitsOperation(
                    ScopeCeiling.ToHashSet(StringComparer.Ordinal), operation));
    }

    /// <summary>
    /// Returns whether durable replay must enforce an OAuth scope ceiling. New non-OAuth records
    /// explicitly persist <see langword="false"/>; an absent legacy marker, a positive marker,
    /// or retained scope data all fail closed as scope-governed.
    /// </summary>
    public bool IsScopeGovernedForReplay()
        => ScopeGoverned is not false
            || OAuthScopes.Count > 0
            || ScopeCeiling.Count > 0;

    private static bool IsBounded(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    private static string? ResolveActor(ClaimsIdentity identity)
        => FirstNonBlank(
            identity.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            identity.FindFirst("sub")?.Value,
            identity.FindFirst(ApiKeyIdClaim)?.Value,
            identity.FindFirst(ApiKeyNameClaim)?.Value,
            identity.Name);

    private static OperationAuthorityContext CreateValidated(
        string? issuer,
        string? membershipIssuer,
        string? actor,
        string? scheme,
        string effectiveTenant,
        IReadOnlyList<string> scopes,
        IReadOnlyList<string> permissions,
        IReadOnlyList<string> roles,
        bool scopeGoverned)
    {
        var authority = new OperationAuthorityContext
        {
            Issuer = issuer ?? string.Empty,
            MembershipIssuer = membershipIssuer,
            Actor = actor ?? string.Empty,
            Scheme = scheme ?? string.Empty,
            EffectiveTenant = effectiveTenant,
            OAuthScopes = scopes,
            ScopeCeiling = scopes,
            Permissions = permissions,
            PermissionCeiling = permissions,
            Roles = roles,
            RoleCeiling = roles,
            ScopeGoverned = scopeGoverned,
        };

        if (!authority.TryValidate(out var error))
        {
            throw new InvalidOperationException($"Operation authority is invalid: {error}");
        }

        return authority;
    }
}

/// <summary>
/// Durable approval decision metadata. The proposer authority is intentionally
/// not replaced by this record when an approved proposal is replayed.
/// </summary>
public sealed record OperationApprovalRecord
{
    /// <summary>Principal that approved or rejected the proposal.</summary>
    public required string Approver { get; init; }

    /// <summary>Whether this record represents approval rather than rejection.</summary>
    public required bool Approved { get; init; }

    /// <summary>When the decision was durably recorded.</summary>
    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>
    /// Whether approved execution retained a validated original proposer authority.
    /// False for legacy proposals without a captured authority and for rejections,
    /// where no execution occurs.
    /// </summary>
    public bool ProposerAuthorityRetained { get; init; }
}
