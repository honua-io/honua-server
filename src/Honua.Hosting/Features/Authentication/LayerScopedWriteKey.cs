// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Infrastructure.Authentication;

// A write grant cannot be a wildcard and confers no read or admin authority, and an execute grant is an exact service and process id with no generic operator, OGC, WPS, or MCP authority.
internal static class LayerScopedWriteKey
{
    public const string WritePermissionPrefix = "write:";

    public const string ReadPermissionPrefix = "read:";

    public const string ExecutePermissionPrefix = "execute:";

    public static bool AllowsGPExecution(ClaimsPrincipal principal, string service, string processId)
        => IsExactExecutionIdentifier(service) && IsExactExecutionIdentifier(processId)
            && principal.FindAll("permission").Any(claim =>
                !IsMalformedResourceGrant(claim.Value)
                && string.Equals(claim.Value.Trim(), $"{ExecutePermissionPrefix}{service}/{processId}", StringComparison.OrdinalIgnoreCase));

    private static bool IsExactExecutionIdentifier(string value)
        => !string.IsNullOrWhiteSpace(value) && value == value.Trim()
            && !value.Any(character => character is '*' or '/' or ':' || char.IsWhiteSpace(character));

    public const string ReadWildcard = "*";

    private static readonly string[] AdminGrants = ["admin", "*"];

    private const string AdminGrantPrefix = "admin:";

    public const string ScopeClaimType = "honua_scope";

    public const string AuthType = "layer-write-key";

    // The scoped write role is not admin, so it never satisfies an admin endpoint.
    public const string Role = "layer-write-key";

    // A scoped key that is not a write key is also not given the admin role.
    public const string ScopedKeyRole = "scoped-api-key";

    // Empty grants normalize to admin:* for bootstrap and the dev bypass; a genuinely scoped grant confers no admin authority.
    public static bool ConfersFullAdmin(IReadOnlyList<string>? permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            // Unscoped: bootstrap admin / dev bypass (null) and empty grant sets
            // (normalized to admin:* by the key store) retain full admin authority.
            return true;
        }

        var hasMeaningfulGrant = false;
        // Not converted to LINQ: the loop both mutates hasMeaningfulGrant and returns
        // early on the first admin grant, so a single Select/Where projection would
        // need to encode two different exit conditions.
        foreach (var trimmed in (permissions).Select(permission => permission?.Trim()))
        {
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            hasMeaningfulGrant = true;
            if (IsAdminGrant(trimmed))
            {
                return true;
            }
        }

        // A grant set consisting solely of blank entries is treated as unscoped.
        return !hasMeaningfulGrant;
    }

    public static bool IsScopedWriteKey(IReadOnlyList<string>? permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            return false;
        }

        var hasWriteGrant = false;
        // Not converted to LINQ: the loop both mutates hasWriteGrant and returns early
        // when an admin grant is found, so it can't collapse into a single Where/Any.
        foreach (var trimmed in (permissions).Select(permission => permission?.Trim()))
        {
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (IsAdminGrant(trimmed))
            {
                return false;
            }

            if (trimmed.StartsWith(WritePermissionPrefix, StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > WritePermissionPrefix.Length)
            {
                hasWriteGrant = true;
            }
        }

        return hasWriteGrant;
    }

    public static bool IsScopedWritePrincipal(ClaimsPrincipal? principal)
        => principal is not null &&
           (principal.HasClaim("auth_type", AuthType) ||
            principal.HasClaim("auth_type", ScopedJobTokenIssuer.AuthTypeClaimValue));

    public static bool IsScopeGovernedPrincipal(ClaimsPrincipal? principal)
        => principal is not null &&
           (IsScopedWritePrincipal(principal)
            || ScopeGovernanceRoles.Any(role =>
                principal.IsInRole(role) || principal.HasClaim(ClaimTypes.Role, role)));

    internal static readonly IReadOnlyList<string> ScopeGovernanceRoles =
    [
        ScopedKeyRole,
        AdminApiKeyPermission.ScopedAdminRole,
        AdminApiKeyPermission.ApprovedOperationRole,
    ];

    public static bool AllowsRead(ClaimsPrincipal principal, string? serviceName, string? layerName)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        var service = serviceName.Trim();
        var layer = layerName?.Trim();
        return principal.FindAll("permission")
            .Any(claim => GrantMatchesResource(claim.Value, ReadPermissionPrefix, service, layer, allowWildcard: true));
    }

    // A service-wide write grant covers every layer of that service; a layer grant covers only the named layer.
    public static bool AllowsWrite(ClaimsPrincipal principal, string? serviceName, string? layerName)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        var service = serviceName.Trim();
        var layer = layerName?.Trim();

        return principal.FindAll("permission")
            .Any(claim => GrantAllowsWrite(claim.Value, service, layer));
    }

    private static bool GrantAllowsWrite(string? grant, string service, string? layer)
        => GrantMatchesResource(grant, WritePermissionPrefix, service, layer, allowWildcard: false);

    // Write grants reject wildcards, execute grants need both ids, and any other prefix is not a resource grant and is not reported as malformed.
    public static bool IsMalformedResourceGrant(string? grant)
    {
        var trimmed = grant?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith(ExecutePermissionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var executionScope = trimmed[ExecutePermissionPrefix.Length..];
            var separator = executionScope.IndexOf('/', StringComparison.Ordinal);
            return separator < 1
                || !IsExactExecutionIdentifier(executionScope[..separator])
                || !IsExactExecutionIdentifier(executionScope[(separator + 1)..]);
        }

        bool allowWildcard;
        string scope;
        if (trimmed.StartsWith(ReadPermissionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            allowWildcard = true;
            scope = trimmed[ReadPermissionPrefix.Length..].Trim();
        }
        else if (trimmed.StartsWith(WritePermissionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            allowWildcard = false;
            scope = trimmed[WritePermissionPrefix.Length..].Trim();
        }
        else
        {
            return false;
        }

        if (scope.Length == 0)
        {
            return true;
        }

        var separatorIndex = scope.IndexOf('/', StringComparison.Ordinal);
        var service = separatorIndex < 0 ? scope : scope[..separatorIndex].Trim();
        var layer = separatorIndex < 0 ? null : scope[(separatorIndex + 1)..].Trim();
        if (service.Length == 0 || layer is { Length: 0 })
        {
            return true;
        }

        var serviceIsWildcard = string.Equals(service, ReadWildcard, StringComparison.Ordinal);
        var layerIsWildcard = string.Equals(layer, ReadWildcard, StringComparison.Ordinal);
        if (!allowWildcard)
        {
            return serviceIsWildcard || layerIsWildcard;
        }

        // read:* is the only service wildcard; read:*/{layer} and read:*/* are not grammar.
        return serviceIsWildcard && layer is not null;
    }

    private static bool GrantMatchesResource(string? grant, string prefix, string service, string? layer, bool allowWildcard)
    {
        var trimmed = grant?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var scope = trimmed[prefix.Length..].Trim();
        if (scope.Length == 0)
        {
            return false;
        }

        var separatorIndex = scope.IndexOf('/');
        if (separatorIndex < 0)
        {
            // read:* authorizes any layer of any service (read grants only).
            if (allowWildcard && string.Equals(scope, ReadWildcard, StringComparison.Ordinal))
            {
                return true;
            }

            // Service-wide grant: {prefix}{service} authorizes any layer of the service.
            return string.Equals(scope, service, StringComparison.OrdinalIgnoreCase);
        }

        var grantService = scope[..separatorIndex].Trim();
        var grantLayer = scope[(separatorIndex + 1)..].Trim();

        if (!string.Equals(grantService, service, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // read:{service}/* is equivalent to the service-wide read:{service} grant,
        // including service-level checks that carry no target layer.
        if (allowWildcard && string.Equals(grantLayer, ReadWildcard, StringComparison.Ordinal))
        {
            return true;
        }

        // A layer-specific grant requires a known target layer that matches.
        return !string.IsNullOrEmpty(layer) &&
               string.Equals(grantLayer, layer, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdminGrant(string permission)
    {
        if (permission.StartsWith(AdminApiKeyPermission.ApprovedOperationGrantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (permission.StartsWith(AdminGrantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var subGrant = permission[AdminGrantPrefix.Length..].Trim();
            return subGrant.Equals("*", StringComparison.OrdinalIgnoreCase)
                || subGrant.Equals("write", StringComparison.OrdinalIgnoreCase)
                || subGrant.Equals("manage", StringComparison.OrdinalIgnoreCase);
        }

        return AdminGrants.Any(grant => string.Equals(permission, grant, StringComparison.OrdinalIgnoreCase));
    }
}
