// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Grammar and helpers for layer-scoped write API keys (#1637).
/// </summary>
/// <remarks>
/// <para>
/// A layer-scoped write key is a low-blast-radius credential intended for
/// public-sandbox or demo-editing scenarios where shipping a full-admin key in
/// page source is unacceptable. It authorizes write operations only on the
/// specific service(s) and layer(s) named in its permission grants and confers
/// no read, metadata, or administrative authority.
/// </para>
/// <para>
/// Permission grammar (case-insensitive, leading/trailing whitespace trimmed):
/// <list type="bullet">
///   <item><c>write:{service}</c> — writes to any layer of the named service.</item>
///   <item><c>write:{service}/{layer}</c> — writes to a single named layer.</item>
/// </list>
/// A key that carries one or more <c>write:</c> grants and does not carry a
/// full-administration grant (<c>admin</c> or <c>*</c>) is treated as a scoped
/// write key: it is authenticated as a non-admin principal and is enforced by
/// the shared write-authorization pipeline rather than by caller discipline.
/// </para>
/// </remarks>
internal static class LayerScopedWriteKey
{
    /// <summary>
    /// Prefix that identifies a layer-scoped write permission grant.
    /// </summary>
    public const string WritePermissionPrefix = "write:";

    /// <summary>
    /// Exact full-administration grants. A key carrying any of these is NOT scoped
    /// and retains full admin authority (legacy behavior).
    /// </summary>
    private static readonly string[] AdminGrants = ["admin", "*"];

    /// <summary>
    /// Prefix for administrative permission grants (e.g. <c>admin:*</c>,
    /// <c>admin:read</c>). A key carrying any such grant retains full admin
    /// authority and is never treated as a layer-scoped write key.
    /// </summary>
    private const string AdminGrantPrefix = "admin:";

    /// <summary>
    /// Claim type used to mark a principal as a layer-scoped write key. The value
    /// is the literal <see cref="AuthType"/> so downstream code can branch on it.
    /// </summary>
    public const string ScopeClaimType = "honua_scope";

    /// <summary>
    /// The <c>auth_type</c> claim value assigned to layer-scoped write keys.
    /// </summary>
    public const string AuthType = "layer-write-key";

    /// <summary>
    /// Role assigned to a layer-scoped write key so it never satisfies the
    /// <c>admin</c> role requirement guarding administrative endpoints.
    /// </summary>
    public const string Role = "layer-write-key";

    /// <summary>
    /// Role assigned to a genuinely-scoped API key whose grants confer neither full
    /// admin authority nor a layer-scoped <c>write:</c> grant (issue #1985). The role
    /// is deliberately not <c>admin</c> so the key is denied any endpoint guarded by
    /// the admin authorization policy; its scoped grants remain available as
    /// <c>permission</c> claims for endpoint-level enforcement.
    /// </summary>
    public const string ScopedKeyRole = "scoped-api-key";

    /// <summary>
    /// Determines whether the supplied permission grants confer full administrative
    /// authority — i.e. the key is unscoped and must satisfy every admin endpoint
    /// (legacy behavior). This is the case when no permissions are supplied (the
    /// password-based bootstrap admin and the development bypass authenticate with
    /// <see langword="null"/> permissions, and the key store normalizes an empty
    /// grant set to <c>admin:*</c>) or when at least one grant is a full-admin grant
    /// (<c>admin</c>, <c>*</c>, or any <c>admin:</c>-prefixed grant such as
    /// <c>admin:*</c>).
    /// </summary>
    /// <remarks>
    /// A key whose grants are genuinely scoped — neither a full-admin grant nor a
    /// layer-scoped <c>write:</c> grant — confers no admin authority. Such a key is
    /// authenticated as a non-admin principal (issue #1985) so it no longer silently
    /// passes the admin authorization policy; its grants are surfaced as
    /// <c>permission</c> claims for endpoint-level enforcement.
    /// </remarks>
    /// <param name="permissions">The key's permission grants.</param>
    /// <returns><see langword="true"/> when the key confers full admin authority.</returns>
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

    /// <summary>
    /// Determines whether the supplied permission grants describe a layer-scoped
    /// write key: at least one <c>write:</c> grant and no full-admin grant.
    /// </summary>
    /// <param name="permissions">The key's permission grants.</param>
    /// <returns><see langword="true"/> when the key is layer-scoped.</returns>
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

    /// <summary>
    /// Determines whether the principal authenticated as a layer-scoped write key.
    /// </summary>
    /// <param name="principal">The request principal.</param>
    /// <returns><see langword="true"/> when the principal is a scoped write key.</returns>
    public static bool IsScopedWritePrincipal(ClaimsPrincipal? principal)
        => principal is not null &&
           principal.HasClaim("auth_type", AuthType);

    /// <summary>
    /// Evaluates whether a scoped write principal is authorized to write to the
    /// supplied <c>(service, layer)</c>. The principal must carry a matching
    /// <c>write:{service}</c> or <c>write:{service}/{layer}</c> grant. A
    /// service-wide grant authorizes any layer of that service; a layer-specific
    /// grant authorizes only the named layer.
    /// </summary>
    /// <param name="principal">The scoped write principal.</param>
    /// <param name="serviceName">The target service name.</param>
    /// <param name="layerName">The target layer name, when known.</param>
    /// <returns><see langword="true"/> when a grant authorizes the write.</returns>
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
    {
        var trimmed = grant?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            !trimmed.StartsWith(WritePermissionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var scope = trimmed[WritePermissionPrefix.Length..].Trim();
        if (scope.Length == 0)
        {
            return false;
        }

        var separatorIndex = scope.IndexOf('/');
        if (separatorIndex < 0)
        {
            // Service-wide grant: write:{service} authorizes any layer of the service.
            return string.Equals(scope, service, StringComparison.OrdinalIgnoreCase);
        }

        var grantService = scope[..separatorIndex].Trim();
        var grantLayer = scope[(separatorIndex + 1)..].Trim();

        if (!string.Equals(grantService, service, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A layer-specific grant requires a known target layer that matches.
        return !string.IsNullOrEmpty(layer) &&
               string.Equals(grantLayer, layer, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdminGrant(string permission)
    {
        if (permission.StartsWith(AdminGrantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AdminGrants.Any(grant => string.Equals(permission, grant, StringComparison.OrdinalIgnoreCase));
    }
}
