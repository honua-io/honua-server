// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
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
    private const string ApiKeyIdClaim = "api_key_id";
    private const string ApiKeyNameClaim = "api_key_name";
    private const string ApiKeyPermissionClaim = "permission";

    /// <summary>Canonical value for a deliberately tenant-less operation context.</summary>
    public const string Tenantless = "$tenantless";

    /// <summary>Canonical token issuer or API-key provider.</summary>
    public required string Issuer { get; init; }

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

    /// <summary>
    /// The maximum permission-grant set available to this operation. It must be a subset of
    /// <see cref="Permissions"/> so approved replay cannot gain an API-key permission.
    /// </summary>
    public IReadOnlyList<string> PermissionCeiling { get; init; } = Array.Empty<string>();

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
        var actor = FirstNonBlank(
            identity.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            identity.FindFirst("sub")?.Value,
            identity.FindFirst(ApiKeyIdClaim)?.Value,
            identity.FindFirst(ApiKeyNameClaim)?.Value,
            identity.Name);
        var issuer = identity.FindFirst("iss")?.Value ?? scheme;
        var scopes = identity.Claims
            .Where(claim => claim.Type is OperatorScopeCatalog.ScopeClaimType
                or OperatorScopeCatalog.ScpClaimType
                or OperatorScopeCatalog.ScopeClaimUri)
            .SelectMany(claim => claim.Value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();
        var permissions = identity.FindAll(ApiKeyPermissionClaim)
            .Select(claim => claim.Value.Trim())
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();

        return CreateValidated(issuer, actor, scheme, effectiveTenant, scopes, permissions);
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
            actor,
            "Service",
            effectiveTenant,
            Array.Empty<string>(),
            Array.Empty<string>());

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

        error = null;
        return true;
    }

    private static bool IsBounded(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    private static OperationAuthorityContext CreateValidated(
        string? issuer,
        string? actor,
        string? scheme,
        string effectiveTenant,
        IReadOnlyList<string> scopes,
        IReadOnlyList<string> permissions)
    {
        var authority = new OperationAuthorityContext
        {
            Issuer = issuer ?? string.Empty,
            Actor = actor ?? string.Empty,
            Scheme = scheme ?? string.Empty,
            EffectiveTenant = effectiveTenant,
            OAuthScopes = scopes,
            ScopeCeiling = scopes,
            Permissions = permissions,
            PermissionCeiling = permissions,
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
