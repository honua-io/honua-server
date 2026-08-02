// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Captures the submitting principal's row/field security identity into the durable
/// <see cref="JobSecurityContext"/> pinned on a background job, and restores it into a
/// <see cref="ClaimsPrincipal"/> on the worker (honua-server#3068).
/// </summary>
/// <remarks>
/// <para>
/// Restoring an equivalent principal — rather than teaching every consumer a second,
/// claims-free code path — is what keeps the background read byte-for-byte identical to the
/// synchronous one: <see cref="RowLevelSecurityFilterSource"/> and <see cref="FieldMaskSource"/>
/// run their existing logic against the restored principal with no behavioral fork.
/// </para>
/// <para>
/// The restored principal is never surfaced as an authenticated request identity. Beyond policy
/// resolution it is also the identity the two deferred-submission lanes authorize against —
/// an approval resume and a cron/event-triggered workflow run — because on both the ambient
/// principal is a synthetic stand-in (name-only, or the orchestrator carrying <c>role=admin</c>)
/// and the real submitter is reachable only through this snapshot. That is sound precisely
/// because the snapshot can only attenuate: it is exactly the claims the submitter presented,
/// so authorizing against it can never grant more than authorizing the submitter live would
/// have (honua-server#3046 review).
/// </para>
/// </remarks>
internal static class JobSecurityContextCapture
{
    /// <summary>Tenant claim type mirrored from the portal-token grammar.</summary>
    private const string TenantClaimType = "tenant_id";

    /// <summary>
    /// Azure/OIDC tenant claim the middleware also accepts. A principal presenting only
    /// <c>tid</c> is tenant-scoped on the live request, so capturing nothing here would restore
    /// a tenant-LESS identity on the deferred lanes and widen the layer gate
    /// (honua-server#3046 review).
    /// </summary>
    private const string AzureTenantClaimType = "tid";

    /// <summary>
    /// Authentication type stamped on a restored identity so it is distinguishable in logs and
    /// can never be confused with a live request identity.
    /// </summary>
    private const string RestoredAuthenticationType = "HonuaJobSecurityContext";

    /// <summary>
    /// Upper bound on captured NON-ROLE claims, so a pathological token cannot bloat every
    /// durable job record.
    /// </summary>
    /// <remarks>
    /// Role claims are deliberately exempt. Capturing roles first is not enough on its own: a
    /// principal with more roles than the budget would have had roles themselves truncated,
    /// and a dropped role that owns an RLS or field-mask policy WIDENS access rather than
    /// narrowing it — <c>RowLevelSecurityFilterSource</c> returns no filter when no policy
    /// matches, and <c>FieldMaskSource</c> returns an empty mask. Truncation must never be
    /// able to grant visibility the live request did not have, so roles are captured in full
    /// and the budget bounds only the descriptive claims alongside them (honua-server#3068
    /// review).
    /// </remarks>
    private const int MaxCapturedNonRoleClaims = 256;

    /// <summary>
    /// Claim types exempt from <see cref="MaxCapturedNonRoleClaims"/> alongside roles, because
    /// dropping them WIDENS authority rather than narrowing it.
    /// </summary>
    /// <remarks>
    /// <c>OperatorScopeCatalog.IsScopeGoverned</c> decides whether a principal is subject to
    /// OAuth scope narrowing by looking for exactly these claims. A principal presenting 256
    /// other claims before them would have had them truncated away, and the restored identity
    /// would then read as UNGOVERNED — so an approval resume or a triggered firing would apply
    /// the captured roles to operations the original token never delegated, skipping the
    /// narrowing entirely. The budget exists to bound record size, never to remove a
    /// restriction (honua-server#3046 review).
    /// </remarks>
    private static readonly HashSet<string> BudgetExemptClaimTypes = new(StringComparer.Ordinal)
    {
        OperatorScopeCatalog.ScopeGovernedClaimType,
        OperatorScopeCatalog.ScopeClaimType,
        OperatorScopeCatalog.ScpClaimType,
        OperatorScopeCatalog.ScopeClaimUri,
    };

    /// <summary>
    /// Claim types never persisted: they carry credentials rather than policy identity, and a
    /// durable job record is not a credential store.
    /// </summary>
    private static readonly HashSet<string> ExcludedClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token",
        "id_token",
        "refresh_token",
        "client_secret",
        "password",
    };

    /// <summary>
    /// Builds the durable snapshot for <paramref name="principal"/>. Always returns a snapshot
    /// (never <see langword="null"/>) — a principal presenting no claims is a meaningful, and
    /// strictly more restrictive, capture than "no snapshot at all", which the catalog-layer read
    /// seam treats as fail-closed.
    /// </summary>
    /// <param name="principal">The submitting principal.</param>
    /// <param name="options">RBAC options declaring the configured role claim type.</param>
    /// <param name="tenantContext">
    /// The effective request tenant resolved by tenant middleware. When present, its value is
    /// authoritative over raw token claims, including an accepted admin header override or a
    /// custom configured tenant-claim type.
    /// </param>
    /// <returns>The captured snapshot.</returns>
    public static JobSecurityContext Capture(
        ClaimsPrincipal principal,
        RbacOptions options,
        ITenantContext? tenantContext = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var roleClaimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ClaimTypes.Role,
            options.EffectiveRoleClaimType,
        };

        var captured = new List<JobSecurityClaim>();
        var seen = new HashSet<(string Type, string Value)>();

        // Roles are policy identity — RLS predicates and field masks key on them — so they are
        // captured in full, with no budget. The OAuth scope-governance claims are exempt for the
        // mirror-image reason: losing them removes the scope narrowing rather than a description.
        // Everything else is descriptive and is what the budget bounds.
        var exemptClaimTypes = new HashSet<string>(roleClaimTypes, StringComparer.OrdinalIgnoreCase);
        exemptClaimTypes.UnionWith(BudgetExemptClaimTypes);

        AppendClaims(principal, captured, seen, exemptClaimTypes, includeMatching: true, limit: null);
        AppendClaims(
            principal, captured, seen, exemptClaimTypes, includeMatching: false,
            limit: captured.Count + MaxCapturedNonRoleClaims);

        var tenantId = tenantContext is null
            ? principal.FindFirstValue(TenantClaimType) ?? principal.FindFirstValue(AzureTenantClaimType)
            : tenantContext.TenantId;

        return new JobSecurityContext(
            principal.Identity?.Name,
            tenantId,
            captured,
            options.EffectiveRoleClaimType);
    }

    /// <summary>
    /// Restores a policy-evaluation principal from a pinned snapshot. The original claim types
    /// are preserved verbatim, so role enumeration and RLS claim lookups resolve exactly as they
    /// did for the live submitter.
    /// </summary>
    /// <param name="context">The pinned snapshot.</param>
    /// <returns>A principal equivalent to the submitter for policy-resolution purposes.</returns>
    public static ClaimsPrincipal Restore(JobSecurityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var claims = context.Claims
            .Where(static claim => !string.IsNullOrWhiteSpace(claim.Type))
            .Select(static claim => new Claim(claim.Type, claim.Value ?? string.Empty))
            .ToList();

        if (!string.IsNullOrWhiteSpace(context.PrincipalId) &&
            !claims.Exists(claim => string.Equals(claim.Type, ClaimTypes.Name, StringComparison.OrdinalIgnoreCase)))
        {
            claims.Add(new Claim(ClaimTypes.Name, context.PrincipalId));
        }

        // Tenant is captured in its own field as well as (ordinarily) among the claims, and the
        // non-role claim budget is applied in enumeration order — so a principal presenting a
        // pathological number of descriptive claims could have had its tenant claim truncated
        // away. Restoring it from the dedicated field keeps the tenant SCOPE on the restored
        // identity, which the deferred-submission lanes authorize against; a tenant-less
        // identity would widen, not narrow (honua-server#3046 review).
        // The dedicated field is the effective tenant resolved by middleware, while the raw
        // captured claims can still contain the token's original tenant. An accepted
        // X-Honua-Tenant override must win on deferred authorization, so replace both standard
        // aliases rather than preserving a stale token value ahead of the authoritative field.
        claims.RemoveAll(claim =>
            string.Equals(claim.Type, TenantClaimType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(claim.Type, AzureTenantClaimType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(context.TenantId))
        {
            claims.Add(new Claim(TenantClaimType, context.TenantId));
            claims.Add(new Claim(AzureTenantClaimType, context.TenantId));
        }

        var roleClaimType = string.IsNullOrWhiteSpace(context.RoleClaimType)
            ? ClaimTypes.Role
            : context.RoleClaimType;

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            RestoredAuthenticationType,
            ClaimTypes.Name,
            roleClaimType));
    }

    private static void AppendClaims(
        ClaimsPrincipal principal,
        List<JobSecurityClaim> captured,
        HashSet<(string Type, string Value)> seen,
        HashSet<string> roleClaimTypes,
        bool includeMatching,
        int? limit)
    {
        foreach (var claim in principal.Claims)
        {
            if (limit is { } ceiling && captured.Count >= ceiling)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(claim.Type) || ExcludedClaimTypes.Contains(claim.Type))
            {
                continue;
            }

            if (roleClaimTypes.Contains(claim.Type) != includeMatching)
            {
                continue;
            }

            if (!seen.Add((claim.Type, claim.Value)))
            {
                continue;
            }

            captured.Add(new JobSecurityClaim(claim.Type, claim.Value));
        }
    }
}
