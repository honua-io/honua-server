// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Infrastructure.Authentication;

// An admin-role principal with no permission claims stays full admin, so bootstrap and client-certificate callers are not locked out, and a layer-scoped write key never reaches this policy.
internal static class AdminApiKeyPermission
{
    public const string PermissionClaimType = "permission";

    private const string AdminGrantPrefix = "admin:";
    private const string OpsGrantPrefix = "ops:";

    // An ops-reader grant is outside the admin family and cannot mutate.
    private static readonly string[] OpsReadGrants = ["ops:read", "ops:reader", "ops:*"];

    private static readonly string[] FullAdminGrants = ["admin", "*", "admin:*"];

    private static readonly string[] WriteSubGrants = ["write", "manage", "*"];

    internal const string ApproveGrant = "admin:approve";
    internal const string ApprovedOperationGrantPrefix = "admin:operation:";
    internal const string ApprovedOperationRole = "approved-operation";
    internal const string ApprovedOperationTenantGrantPrefix = "admin:operation:tenant:";
    internal const string ApprovedOperationTenantClaim = "honua:approved-operation-tenant";

    internal const string ScopedAdminRole = "scoped-admin-key";

    private const string AdminApiPathPrefix = "/api/v1/admin/";

    internal enum AdminAccessLevel
    {
        None = 0,

        Read = 1,

        Write = 2,
    }

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

    // An approved-operation credential is not an unrestricted writer: its admin role is bound to one server-issued method and path.
    internal static bool IsFullAdminPrincipal(ClaimsPrincipal principal)
        => ResolveAccessLevel(principal) == AdminAccessLevel.Write;

    public static bool IsAuthorized(ClaimsPrincipal principal, string? httpMethod)
        => IsAuthorized(principal, httpMethod, requestPath: null);

    public static bool IsAuthorized(ClaimsPrincipal principal, string? httpMethod, string? requestPath)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Approved-operation grants exist only for admin API paths; the factory THROWS
        // for any other path, and this method runs for every authenticated request —
        // computing the grant unconditionally turned all non-admin traffic (health,
        // metrics, protocol endpoints) into 400s (trunk red at bf7ce4956).
        if (!string.IsNullOrWhiteSpace(httpMethod)
            && requestPath?.StartsWith(AdminApiPathPrefix, StringComparison.Ordinal) == true)
        {
            var expectedGrant = CreateApprovedOperationGrant(httpMethod, requestPath);
            if (principal.FindAll(PermissionClaimType).Any(claim =>
                    string.Equals(claim.Value, expectedGrant, StringComparison.Ordinal)))
            {
                return true;
            }
        }

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

    // Creating, rotating, or revoking credentials needs full admin; admin:write and admin:manage may read credentials but cannot mint them.
    internal static bool HasCredentialManagementAuthority(ClaimsPrincipal principal, string? httpMethod, string? requestPath)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var permissions = principal.FindAll(PermissionClaimType).Select(claim => claim.Value?.Trim()).ToList();
        if (permissions.Count == 0)
        {
            return principal.IsInRole("admin");
        }

        if (!string.IsNullOrWhiteSpace(httpMethod)
            && requestPath?.StartsWith(AdminApiPathPrefix, StringComparison.Ordinal) == true)
        {
            var expectedGrant = CreateApprovedOperationGrant(httpMethod, requestPath);
            if (permissions.Any(grant => string.Equals(grant, expectedGrant, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return permissions.Any(grant =>
            FullAdminGrants.Any(full => string.Equals(grant, full, StringComparison.OrdinalIgnoreCase)));
    }

    internal static string CreateApprovedOperationGrant(string httpMethod, string requestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(httpMethod);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        if (!requestPath.StartsWith(AdminApiPathPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Approved operation credentials must target an admin API path.", nameof(requestPath));
        }

        return $"{ApprovedOperationGrantPrefix}{httpMethod.ToUpperInvariant()}:{requestPath}";
    }

    internal static string[] CreateApprovedOperationGrants(string httpMethod, string requestPath, string? tenantId)
    {
        // An explicit empty binding preserves accepted single-tenant invocations.
        return [CreateApprovedOperationGrant(httpMethod, requestPath), ApprovedOperationTenantGrantPrefix + tenantId];
    }

    internal static bool IsApprovedOperationGrant(string? grant)
        => grant?.Trim().StartsWith(ApprovedOperationGrantPrefix, StringComparison.Ordinal) == true;

    public static bool HasApproveGrant(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindAll(PermissionClaimType).Any(claim =>
            string.Equals(claim.Value?.Trim(), ApproveGrant, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSafeMethod(string? httpMethod)
        => string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase)
           || string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
           || string.Equals(httpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    public static bool HasOpsReadGrant(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Not converted to LINQ Select/Where: the loop body has an early continue plus two
        // distinct return-true conditions, so a projection+filter chain would be less readable.
        foreach (var trimmed in (principal.FindAll(PermissionClaimType)).Select(claim => claim.Value?.Trim()))
        {
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (OpsReadGrants.Any(grant => string.Equals(trimmed, grant, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
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

    internal static bool IsAdministrativeGrant(string? grant)
        => ClassifyGrant(grant) != AdminAccessLevel.None;

    private static AdminAccessLevel ClassifyGrant(string? grant)
    {
        var trimmed = grant?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return AdminAccessLevel.None;
        }

        if (FullAdminGrants.Any(full => string.Equals(trimmed, full, StringComparison.OrdinalIgnoreCase)))
        {
            return AdminAccessLevel.Write;
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

        if (WriteSubGrants.Any(write => string.Equals(sub, write, StringComparison.OrdinalIgnoreCase)))
        {
            return AdminAccessLevel.Write;
        }

        if (string.Equals(sub, "read", StringComparison.OrdinalIgnoreCase))
        {
            return AdminAccessLevel.Read;
        }

        if (IsApprovedOperationGrant(trimmed))
        {
            return AdminAccessLevel.None;
        }

        // An unrecognized admin sub-grant (e.g. admin:users) is conservatively
        // treated as read-only: it proves admin intent but not write intent, so it
        // can observe but not mutate until the grant vocabulary is extended.
        return AdminAccessLevel.Read;
    }
}
