// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Canonical authority rule for exchanging a managed API key for a portal token
/// (#4577). Shared by the <c>generateToken</c> admin credential bridge and the
/// OAuth2 <c>client_credentials</c> API-key fallback so neither exchange can widen
/// the key.
/// </summary>
/// <remarks>
/// A portal token carries roles only. <see cref="ApiKeyAuthenticationHandler"/>
/// represents a constrained key as a non-admin role plus permission, write-scope or
/// approved-operation claims, none of which a token can hold, and never turns a
/// permission label into a role. Only a full-admin key is therefore representable:
/// it maps to the admin role, exactly as on the <c>X-API-Key</c> transport. Every
/// other key is refused rather than converted.
/// </remarks>
internal static class ManagedKeyExchangeAuthority
{
    private static readonly string[] AdminRoles = ["admin"];

    /// <summary>
    /// Resolves the roles a portal token exchanged from a managed key may carry.
    /// </summary>
    /// <param name="permissions">The validated key record's permission grants.</param>
    /// <returns>
    /// The admin role for a full-admin key; <see langword="null"/> when the key's
    /// authority cannot be preserved by a token and the exchange must be refused.
    /// </returns>
    public static IReadOnlyList<string>? ResolveTokenRoles(IReadOnlyList<string> permissions)
    {
        // Same grant grammar and approved-operation precedence as the API-key
        // handler: an operation/tenant-bound replay key is not full admin even when
        // it also holds an admin grant.
        if (!LayerScopedWriteKey.ConfersFullAdmin(permissions) ||
            permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant))
        {
            return null;
        }

        return AdminRoles;
    }
}
