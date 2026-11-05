// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Identity.Abstractions;
using Microsoft.AspNetCore.Authentication;

namespace Honua.Infrastructure.Authentication;

// Roles are replaced from the managed-user store on each request so a deprovisioned principal does not keep its sign-in roles.
internal static class ManagedMembershipRevalidation
{
    public static async Task<ClaimsPrincipal> ApplyAsync(
        ClaimsPrincipal principal,
        IPrincipalMembershipSource? source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (source is null || principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return principal;
        }

        var authType = identity.FindFirst("auth_type")?.Value;
        if (authType is "admin" or "admin-api-key" or "dev-bypass" or "client-certificate"
            || authType is LayerScopedWriteKey.AuthType or ScopedJobTokenIssuer.AuthTypeClaimValue)
        {
            return principal;
        }

        var principalId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? identity.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return principal;
        }

        PrincipalMembership? membership;
        try
        {
            membership = await source
                .ResolveMembershipAsync(principalId, identity.FindFirst("iss")?.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unauthenticated(identity);
        }

        if (membership is null)
        {
            return principal;
        }

        if (!membership.IsActive)
        {
            return Unauthenticated(identity);
        }

        var roleClaimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ClaimTypes.Role,
            "role",
            "roles",
            identity.RoleClaimType,
        };
        var claims = identity.Claims.Where(claim => !roleClaimTypes.Contains(claim.Type)).ToList();
        foreach (var role in membership.Roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Select(static role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            identity.AuthenticationType,
            identity.NameClaimType,
            ClaimTypes.Role));
    }

    private static ClaimsPrincipal Unauthenticated(ClaimsIdentity identity)
    {
        var claims = identity.Claims.Where(static claim =>
            !string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(claim.Type, "role", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(claim.Type, "roles", StringComparison.OrdinalIgnoreCase));
        return new ClaimsPrincipal(new ClaimsIdentity(claims));
    }
}

internal sealed class ManagedMembershipClaimsTransformation(IPrincipalMembershipSource? membershipSource = null)
    : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        => ManagedMembershipRevalidation.ApplyAsync(principal, membershipSource);
}
