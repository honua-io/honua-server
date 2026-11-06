// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// A portal token can carry roles only, so a constrained key is refused rather than exchanged and only a full-admin key maps to the admin role.
internal static class ManagedKeyExchangeAuthority
{
    private static readonly string[] AdminRoles = ["admin"];

    // Referrer and IP restrictions cannot be represented on the portal token, so a key that has them is refused.
    public static IReadOnlyList<string>? ResolveTokenRoles(AdminApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A portal token is checked against the binding chosen at issuance, not the key's
        // referrer/IP restrictions, so exchanging a restricted key would shed them.
        return ApiKeyRequestRestrictions.IsRestricted(record) ? null : ResolveTokenRoles(record.Permissions);
    }

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
