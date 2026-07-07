// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Grammar and helpers for scoped <em>admin</em> API-key permission grants (#1985).
/// </summary>
/// <remarks>
/// <para>
/// An admin API key carries one or more permission grants (persisted on the key
/// record and projected onto the principal as <c>permission</c> claims). Before
/// #1985 these grants were stored but never enforced: every authenticated admin
/// key was stamped with the <c>admin</c> role and therefore satisfied the
/// <c>Admin</c> authorization policy regardless of how narrowly it was scoped, so
/// a key minted as read-only (<c>admin:read</c>) could still mutate users, keys,
/// deploys, and configuration.
/// </para>
/// <para>
/// This helper interprets the admin grammar so the shared admin authorization
/// policy can enforce it:
/// <list type="bullet">
///   <item><c>admin</c>, <c>*</c>, <c>admin:*</c> — full admin (read and write).</item>
///   <item><c>admin:write</c>, <c>admin:manage</c> — admin write (implies read).</item>
///   <item><c>admin:read</c> — admin read only (safe HTTP methods).</item>
/// </list>
/// A principal that carries the <c>admin</c> role but no <c>permission</c> claims
/// at all (the bootstrap <c>HONUA_ADMIN_PASSWORD</c> key, a client certificate, or
/// the Test dev-bypass) is treated as full admin so the legacy single-admin model
/// is preserved and currently-working keys are never locked out. Layer-scoped
/// write keys (#1637) never reach this policy because they authenticate with a
/// non-admin role.
/// </para>
/// </remarks>
internal static class AdminApiKeyPermission
{
    /// <summary>
    /// Claim type used to project a key's permission grants onto the principal.
    /// </summary>
    public const string PermissionClaimType = "permission";

    private const string AdminGrantPrefix = "admin:";
    private const string OpsGrantPrefix = "ops:";

    /// <summary>
    /// Ops-reader grants that confer read-only access to the operational observability surfaces
    /// (aggregated operate status, ops-health, findings, alerts) but no mutating authority — a
    /// credential distinct from the admin family so a status/monitoring copilot can read the ops
    /// posture without holding a key that could <c>POST</c> a rollback, promotion, or suppression.
    /// </summary>
    private static readonly string[] OpsReadGrants = ["ops:read", "ops:reader", "ops:*"];

    /// <summary>
    /// Grants that confer full administration (read and write).
    /// </summary>
    private static readonly string[] FullAdminGrants = ["admin", "*", "admin:*"];

    /// <summary>
    /// Admin sub-grants that authorize mutating operations (each implies read).
    /// </summary>
    private static readonly string[] WriteSubGrants = ["write", "manage", "*"];

    /// <summary>
    /// Describes how much admin authority a principal's grants confer.
    /// </summary>
    internal enum AdminAccessLevel
    {
        /// <summary>No admin grant; the principal cannot reach admin surfaces.</summary>
        None = 0,

        /// <summary>Read-only admin (safe HTTP methods only).</summary>
        Read = 1,

        /// <summary>Full admin (read and write/mutate).</summary>
        Write = 2,
    }

    /// <summary>
    /// Computes the admin access level a principal's grants confer. A principal in
    /// the <c>admin</c> role with no <c>permission</c> claims is treated as full
    /// admin (bootstrap password, client certificate, or Test dev-bypass).
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The effective <see cref="AdminAccessLevel"/>.</returns>
    public static AdminAccessLevel ResolveAccessLevel(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var sawPermissionClaim = false;
        var level = AdminAccessLevel.None;

        foreach (var claim in principal.FindAll(PermissionClaimType))
        {
            sawPermissionClaim = true;
            var grantLevel = ClassifyGrant(claim.Value);
            if (grantLevel > level)
            {
                level = grantLevel;
            }
        }

        // A key carrying scoped grants is bounded by those grants. A principal with
        // no permission claims (bootstrap password / client cert / dev-bypass) but
        // the admin role retains full admin authority, preserving legacy behavior.
        if (!sawPermissionClaim && principal.IsInRole("admin"))
        {
            return AdminAccessLevel.Write;
        }

        return level;
    }

    /// <summary>
    /// Determines whether a principal is authorized for an admin request whose
    /// HTTP method is <paramref name="httpMethod"/>. Safe (read) methods require at
    /// least <see cref="AdminAccessLevel.Read"/>; mutating methods require
    /// <see cref="AdminAccessLevel.Write"/>.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <param name="httpMethod">The request HTTP method.</param>
    /// <returns><see langword="true"/> when the grants authorize the request.</returns>
    public static bool IsAuthorized(ClaimsPrincipal principal, string? httpMethod)
    {
        var level = ResolveAccessLevel(principal);
        if (level == AdminAccessLevel.None)
        {
            return false;
        }

        if (level == AdminAccessLevel.Write)
        {
            return true;
        }

        // Read-only admin: permit only safe, non-mutating HTTP methods.
        return IsSafeMethod(httpMethod);
    }

    /// <summary>
    /// Returns <see langword="true"/> for HTTP methods that do not mutate state and
    /// are therefore permitted for a read-only admin key.
    /// </summary>
    /// <param name="httpMethod">The request HTTP method.</param>
    /// <returns><see langword="true"/> when the method is safe/read-only.</returns>
    public static bool IsSafeMethod(string? httpMethod)
        => string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase)
           || string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
           || string.Equals(httpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a principal carries a read-only <c>ops:</c> grant (<c>ops:read</c>,
    /// <c>ops:reader</c>, or <c>ops:*</c>). This is a strictly weaker, ops-scoped credential than the
    /// admin family: it authorizes the read-only operational surfaces but never a mutating operation,
    /// and it confers no access to the broader admin surfaces (users, keys, configuration) that
    /// <c>admin:read</c> can observe.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns><see langword="true"/> when a read-only ops grant is present.</returns>
    public static bool HasOpsReadGrant(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        foreach (var claim in principal.FindAll(PermissionClaimType))
        {
            var trimmed = claim.Value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            foreach (var grant in OpsReadGrants)
            {
                if (string.Equals(trimmed, grant, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // An unrecognized ops sub-grant (e.g. a future ops:deploy) still proves ops-read intent:
            // it can observe the ops surfaces but, being non-admin, can never satisfy a mutating
            // requirement (which requires full admin write).
            if (trimmed.StartsWith(OpsGrantPrefix, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > OpsGrantPrefix.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a principal is authorized for an ops-observability request whose HTTP method
    /// is <paramref name="httpMethod"/>. Safe (read) methods are authorized by an admin key of any
    /// level (full or <c>admin:read</c>) or by a read-only <c>ops:</c> grant; mutating methods require
    /// full admin write. This is the enforcement primitive behind the ops-reader authorization policy.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <param name="httpMethod">The request HTTP method.</param>
    /// <returns><see langword="true"/> when the grants authorize the request.</returns>
    public static bool IsOpsReadAuthorized(ClaimsPrincipal principal, string? httpMethod)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var level = ResolveAccessLevel(principal);

        // Full admin authorizes everything, including mutating ops operations.
        if (level == AdminAccessLevel.Write)
        {
            return true;
        }

        // Mutating ops operations require full admin write; a read-only admin or ops grant cannot pass.
        if (!IsSafeMethod(httpMethod))
        {
            return false;
        }

        // Safe method: a read-only admin grant or a read-only ops grant is sufficient.
        return level == AdminAccessLevel.Read || HasOpsReadGrant(principal);
    }

    private static AdminAccessLevel ClassifyGrant(string? grant)
    {
        var trimmed = grant?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return AdminAccessLevel.None;
        }

        foreach (var full in FullAdminGrants)
        {
            if (string.Equals(trimmed, full, StringComparison.OrdinalIgnoreCase))
            {
                return AdminAccessLevel.Write;
            }
        }

        if (!trimmed.StartsWith(AdminGrantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Non-admin grant (e.g. a layer-scoped write: grant). Confers no admin authority.
            return AdminAccessLevel.None;
        }

        var sub = trimmed[AdminGrantPrefix.Length..].Trim();
        if (sub.Length == 0)
        {
            return AdminAccessLevel.None;
        }

        foreach (var write in WriteSubGrants)
        {
            if (string.Equals(sub, write, StringComparison.OrdinalIgnoreCase))
            {
                return AdminAccessLevel.Write;
            }
        }

        if (string.Equals(sub, "read", StringComparison.OrdinalIgnoreCase))
        {
            return AdminAccessLevel.Read;
        }

        // An unrecognized admin sub-grant (e.g. admin:users) is conservatively
        // treated as read-only: it proves admin intent but not write intent, so it
        // can observe but not mutate until the grant vocabulary is extended.
        return AdminAccessLevel.Read;
    }
}
