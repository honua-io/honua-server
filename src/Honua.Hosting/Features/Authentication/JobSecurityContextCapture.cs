// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

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
/// The restored principal is a policy-evaluation identity ONLY. It is never used to authorize an
/// operation (layer read authorization is a submit-time gate, honua-server#3046) and it is never
/// surfaced as an authenticated request identity.
/// </para>
/// </remarks>
internal static class JobSecurityContextCapture
{
    /// <summary>Tenant claim type mirrored from the portal-token grammar.</summary>
    private const string TenantClaimType = "tenant_id";

    /// <summary>
    /// Authentication type stamped on a restored identity so it is distinguishable in logs and
    /// can never be confused with a live request identity.
    /// </summary>
    private const string RestoredAuthenticationType = "HonuaJobSecurityContext";

    /// <summary>
    /// Upper bound on captured claims, so a pathological token cannot bloat every durable job
    /// record. Role claims are captured first and are therefore never the ones dropped.
    /// </summary>
    private const int MaxCapturedClaims = 256;

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
    /// <returns>The captured snapshot.</returns>
    public static JobSecurityContext Capture(ClaimsPrincipal principal, RbacOptions options)
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

        // Roles first: field masking keys purely on roles, so they must never be the claims a
        // budget overflow drops.
        AppendClaims(principal, captured, seen, roleClaimTypes, includeMatching: true);
        AppendClaims(principal, captured, seen, roleClaimTypes, includeMatching: false);

        return new JobSecurityContext(
            principal.Identity?.Name,
            principal.FindFirstValue(TenantClaimType),
            captured);
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

        return new ClaimsPrincipal(new ClaimsIdentity(claims, RestoredAuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }

    private static void AppendClaims(
        ClaimsPrincipal principal,
        List<JobSecurityClaim> captured,
        HashSet<(string Type, string Value)> seen,
        HashSet<string> roleClaimTypes,
        bool includeMatching)
    {
        foreach (var claim in principal.Claims)
        {
            if (captured.Count >= MaxCapturedClaims)
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
