// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

// The restored principal is never a live request identity, and truncating captured roles would widen access because a missing row or field policy applies no filter.
internal static class JobSecurityContextCapture
{
    private const string TenantClaimType = "tenant_id";

    private const string AzureTenantClaimType = "tid";

    // The authentication type marks a restored identity so it is not treated as a live request.
    private const string RestoredAuthenticationType = "HonuaJobSecurityContext";
    internal const string CapturedAuthenticationClaimType = "honua:job-authenticated";

    // Role claims are exempt from the non-role claim budget because dropping a role that owns a row or field policy widens access.
    private const int MaxCapturedNonRoleClaims = 256;

    // Operator-scope claims are exempt because dropping them makes the principal look ungoverned and skips scope narrowing.
    private static readonly HashSet<string> BudgetExemptClaimTypes = new(StringComparer.Ordinal)
    {
        OperatorScopeCatalog.ScopeGovernedClaimType,
        OperatorScopeCatalog.ScopeClaimType,
        OperatorScopeCatalog.ScpClaimType,
        OperatorScopeCatalog.ScopeClaimUri,

        // The validated issuer scopes the subject used for live role membership. Losing it
        // could resolve a same-valued subject from another IdP, so it is authorization
        // identity rather than a budgeted descriptive claim.
        "iss",

        // The managed-membership marker is budget-exempt for the same "dropping it WIDENS
        // authority" reason as the scope-governance claims: if a pathological token pushed the
        // marker past the non-role claim budget, the durable snapshot would lose it and a later
        // unresolved revalidation would fall back to the captured roles (fail OPEN) instead of
        // failing closed. Truncation must never be able to relax a restriction (honua-server#3081).
        JobSecurityContextClaimTypes.ManagedMembershipMarker,
    };

    // Credential claim types are never persisted; a durable job record is not a credential store.
    private static readonly HashSet<string> ExcludedClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token",
        "id_token",
        "refresh_token",
        "client_secret",
        "password",
        CapturedAuthenticationClaimType,
        JobSecurityContextClaimTypes.MembershipPrincipalId,
    };

    // A principal with no claims is still snapshotted, because a missing snapshot is treated as less restrictive, and the snapshot tenant is the middleware-resolved tenant.
    public static JobSecurityContext Capture(
        ClaimsPrincipal principal,
        RbacOptions options,
        ITenantContext? tenantContext = null,
        bool membershipManaged = false)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var roleClaimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ClaimTypes.Role,
            options.EffectiveRoleClaimType,
        };

        if (roleClaimTypes.Contains(CapturedAuthenticationClaimType))
        {
            throw new InvalidOperationException(
                "The job authentication-state claim type cannot be configured as a role claim type.");
        }

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

        // Authentication state is stamped from the submitting identity, outside the claim
        // budget. The input claim type is excluded so only capture can set this marker.
        captured.Add(new JobSecurityClaim(CapturedAuthenticationClaimType,
            principal.Identity?.IsAuthenticated == true ? bool.TrueString : bool.FalseString));

        // Stamp the managed-membership marker when the source owns this principal's roles. It is
        // budget-exempt (see BudgetExemptClaimTypes) so it is captured here in full and can never
        // be truncated into a fail-open (honua-server#3081).
        if (membershipManaged &&
            !captured.Any(claim => string.Equals(
                claim.Type, JobSecurityContextClaimTypes.ManagedMembershipMarker, StringComparison.Ordinal)))
        {
            captured.Add(new JobSecurityClaim(
                JobSecurityContextClaimTypes.ManagedMembershipMarker,
                JobSecurityContextClaimTypes.ManagedMembershipMarkerValue));
        }

        // PrincipalId is durable canonical attribution and can be issuer-qualified. Membership
        // stores instead key on the raw normalized subject/name accepted at capture time. Persist
        // that lookup key separately after stripping any input copy of this framework-owned claim,
        // so deferred revalidation neither queries with the canonical display value nor trusts a
        // caller-supplied membership identifier.
        var membershipPrincipalId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(membershipPrincipalId))
        {
            captured.Add(new JobSecurityClaim(
                JobSecurityContextClaimTypes.MembershipPrincipalId,
                membershipPrincipalId));
        }

        // Detached polling retains the principal after HttpContext disposal. A trusted
        // middleware binding preserves its effective tenant, including an explicitly
        // unscoped request whose raw token still carries a tenant claim.
        var hasRequestBinding = CanonicalSecurityActor.FindStampedValue(
            principal, CanonicalSecurityActor.CanonicalActorClaim) is not null;
        var detachedTenant = hasRequestBinding
            ? CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.EffectiveTenantClaim)
            : principal.FindFirstValue(TenantClaimType) ?? principal.FindFirstValue(AzureTenantClaimType);
        var tenantId = tenantContext is null ? detachedTenant : tenantContext.TenantId;

        return new JobSecurityContext(
            CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.CanonicalActorClaim)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub")
                ?? principal.Identity?.Name,
            tenantId,
            captured,
            options.EffectiveRoleClaimType)
        {
            OwnerActorId = JobOwnershipSecurity.ResolveOwner(principal),
            WorkspaceOwnerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub")
                ?? principal.FindFirstValue("api_key_id")
                ?? principal.Identity?.Name
                ?? "anonymous"
        };
    }

    public static ClaimsPrincipal Restore(JobSecurityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var claims = context.Claims
            .Where(static claim => !string.IsNullOrWhiteSpace(claim.Type)
                && !string.Equals(claim.Type, CapturedAuthenticationClaimType, StringComparison.OrdinalIgnoreCase))
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

        // The API-key handler stamps its scope-governance markers (scoped-api-key,
        // scoped-admin-key, approved-operation) as ClaimTypes.Role, while the snapshot records
        // the configured RBAC role claim type (OIDC "roles" by default). Restoring with that type
        // made IsInRole miss the markers, so a scoped key resumed on a deferred lane (async print,
        // GP approval resume, workflow firing) read as an ungoverned authenticated principal and
        // bypassed its permission grants. Mirror each marker found under either type onto the
        // restored role claim type. This also covers snapshots written before the fix, and it can
        // only narrow access: the markers restrict a principal to its grants (honua-server#6101).
        if (!string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            var governanceMarkers = claims
                .Where(claim => (string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(claim.Type, roleClaimType, StringComparison.OrdinalIgnoreCase))
                    && LayerScopedWriteKey.ScopeGovernanceRoles.Contains(claim.Value, StringComparer.Ordinal))
                .Select(static claim => claim.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var marker in governanceMarkers)
            {
                if (!claims.Exists(claim => string.Equals(claim.Type, roleClaimType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(claim.Value, marker, StringComparison.Ordinal)))
                {
                    claims.Add(new Claim(roleClaimType, marker));
                }
            }
        }

        // Older snapshots predate anonymous print submissions and retain their authentication
        // behavior. New snapshots preserve the actual submission authentication state.
        var authenticated = context.Claims
            .Where(static claim => string.Equals(claim.Type, CapturedAuthenticationClaimType, StringComparison.OrdinalIgnoreCase))
            .All(static claim => string.Equals(claim.Value, bool.TrueString, StringComparison.Ordinal));
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            authenticated ? RestoredAuthenticationType : null,
            ClaimTypes.Name,
            roleClaimType));
    }

    // Live revalidation replaces only roles, and a source exception propagates so an outage is not treated as an allow.
    public static async Task<JobSecurityContextMembershipResult> RevalidateRoleMembershipAsync(
        JobSecurityContext context,
        IPrincipalMembershipSource? source,
        RbacOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        // Resolution of a managed identity can miss in three ways: no membership authority is
        // configured, the snapshot carries no identifier the authority can key on, or the
        // authority is asked and returns nothing. The first is the documented no-authority mode
        // that always snapshot-falls-back. The other two are a genuine miss: for a NON-managed
        // identity that is still the documented fallback, but for a managed one (carrying the
        // marker) it means the source that OWNS this principal's roles could not confirm them
        // HERE — a node-local store on another replica, or an identifier the store cannot match —
        // and trusting the captured roles would let a revoked or deactivated managed identity keep
        // authorizing deferred work. Fail closed in that case: strip the roles and report an
        // explicit unresolved status. HasRemovedRoles is set so the existing deferred-lane callers
        // refuse the operation through their role-removal branch unchanged (honua-server#3081).
        JobSecurityContextMembershipResult SnapshotFallbackOrFailClosed() =>
            SnapshotIndicatesManagedMembership(context)
                ? new JobSecurityContextMembershipResult(
                    StripRoleClaims(context, options),
                    JobSecurityContextMembershipStatus.ManagedUnresolved,
                    HasRemovedRoles: true)
                : new JobSecurityContextMembershipResult(
                    context,
                    JobSecurityContextMembershipStatus.SnapshotFallback);

        if (source is null)
        {
            // No membership authority configured at all: the marker's premise (roles owned by a
            // live source) does not hold, so this is the documented snapshot-fallback mode.
            return new JobSecurityContextMembershipResult(
                context,
                JobSecurityContextMembershipStatus.SnapshotFallback);
        }

        var principalId = ResolveMembershipPrincipalId(context);
        if (principalId is null)
        {
            return SnapshotFallbackOrFailClosed();
        }

        var membership = await source
            .ResolveMembershipAsync(principalId, ResolveMembershipIssuer(context), cancellationToken)
            .ConfigureAwait(false);
        if (membership is null)
        {
            return SnapshotFallbackOrFailClosed();
        }

        var roleClaimType = options.EffectiveRoleClaimType;
        var roleClaimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ClaimTypes.Role,
            roleClaimType,
        };
        if (!string.IsNullOrWhiteSpace(context.RoleClaimType))
        {
            roleClaimTypes.Add(context.RoleClaimType);
        }

        var snapshotRoles = context.Claims
            .Where(claim => roleClaimTypes.Contains(claim.Type) && !string.IsNullOrWhiteSpace(claim.Value))
            .Select(static claim => claim.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentRoles = membership.IsActive
            ? membership.Roles
                .Where(static role => !string.IsNullOrWhiteSpace(role))
                .Select(static role => role.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        var membershipIsCurrent = membership.IsActive && snapshotRoles.SetEquals(currentRoles);

        var claims = context.Claims
            .Where(claim => !roleClaimTypes.Contains(claim.Type))
            .Concat(currentRoles.Select(role => new JobSecurityClaim(roleClaimType, role)))
            .ToArray();
        var revalidated = context with { Claims = claims, RoleClaimType = roleClaimType };

        return new JobSecurityContextMembershipResult(
            revalidated,
            membershipIsCurrent
                ? JobSecurityContextMembershipStatus.Current
                : membership.IsActive
                    ? JobSecurityContextMembershipStatus.Changed
                    : JobSecurityContextMembershipStatus.Inactive,
            HasRemovedRoles: snapshotRoles.Except(currentRoles, StringComparer.OrdinalIgnoreCase).Any());
    }

    private static string? ResolveMembershipPrincipalId(JobSecurityContext context)
    {
        var principalId = context.Claims.FirstOrDefault(claim =>
            string.Equals(
                claim.Type,
                JobSecurityContextClaimTypes.MembershipPrincipalId,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(claim.Value))?.Value;
        if (principalId is not null)
        {
            return principalId;
        }

        principalId = context.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(claim.Value))?.Value;
        if (principalId is not null)
        {
            return principalId;
        }

        principalId = context.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, "sub", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(claim.Value))?.Value;
        if (principalId is not null)
        {
            return principalId;
        }

        principalId = context.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, ClaimTypes.Name, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(claim.Value))?.Value;
        if (principalId is not null)
        {
            return principalId;
        }

        return string.IsNullOrWhiteSpace(context.PrincipalId) ? null : context.PrincipalId;
    }

    private static string? ResolveMembershipIssuer(JobSecurityContext context)
        => context.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, "iss", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(claim.Value))?.Value;

    // A managed-membership marker makes a later unresolved revalidation fail closed; a null source or a source error stays unmarked and remains a snapshot fallback.
    public static async Task<bool> IsManagedMembershipAsync(
        ClaimsPrincipal principal,
        IPrincipalMembershipSource? source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (source is null)
        {
            return false;
        }

        var principalId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return false;
        }

        try
        {
            var issuer = principal.FindFirstValue("iss");
            return await source.ResolveMembershipAsync(principalId, issuer, cancellationToken).ConfigureAwait(false)
                is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool SnapshotIndicatesManagedMembership(JobSecurityContext context) =>
        context.Claims.Any(claim =>
            string.Equals(
                claim.Type,
                JobSecurityContextClaimTypes.ManagedMembershipMarker,
                StringComparison.Ordinal)
            && string.Equals(
                claim.Value,
                JobSecurityContextClaimTypes.ManagedMembershipMarkerValue,
                StringComparison.OrdinalIgnoreCase));

    private static JobSecurityContext StripRoleClaims(JobSecurityContext context, RbacOptions options)
    {
        var roleClaimType = options.EffectiveRoleClaimType;
        var roleClaimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ClaimTypes.Role,
            roleClaimType,
        };
        if (!string.IsNullOrWhiteSpace(context.RoleClaimType))
        {
            roleClaimTypes.Add(context.RoleClaimType);
        }

        var claims = context.Claims
            .Where(claim => !roleClaimTypes.Contains(claim.Type))
            .ToArray();
        return context with { Claims = claims, RoleClaimType = roleClaimType };
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

internal enum JobSecurityContextMembershipStatus
{
    SnapshotFallback,

    Current,

    Changed,

    Inactive,

    // A managed identity the source cannot resolve fails closed instead of trusting the captured roles.
    ManagedUnresolved,
}

// A deferred caller fails closed when a captured role is now absent, because a smaller set can select a less restrictive row or field policy.
internal sealed record JobSecurityContextMembershipResult(
    JobSecurityContext Context,
    JobSecurityContextMembershipStatus Status,
    bool HasRemovedRoles = false);
