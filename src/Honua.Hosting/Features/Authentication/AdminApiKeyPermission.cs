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
