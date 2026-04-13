// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal static class AdminAuthClaimsProjector
{
    public static ClaimsPrincipal CreatePrincipal(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        string authenticationScheme,
        string authTypeClaimValue = "oidc")
    {
        ArgumentNullException.ThrowIfNull(sessionClaims);

        var claims = sessionClaims
            .Select(static claim => new Claim(claim.Type, claim.Value))
            .ToList();

        NormalizeClaims(claims, authTypeClaimValue);

        var identity = new ClaimsIdentity(
            claims,
            string.IsNullOrWhiteSpace(authenticationScheme) ? "oidc" : authenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    public static bool TryProjectValidatedClaims(
        IEnumerable<Claim> sourceClaims,
        out IReadOnlyList<AdminAuthSessionClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(sourceClaims);

        var projectedClaims = sourceClaims.ToList();

        if (projectedClaims.Count == 0)
        {
            claims = [];
            return false;
        }

        NormalizeClaims(projectedClaims, "oidc");

        claims = projectedClaims
            .GroupBy(static claim => $"{claim.Type}\u001f{claim.Value}", StringComparer.Ordinal)
            .Select(static group => new AdminAuthSessionClaim
            {
                Type = group.First().Type,
                Value = group.First().Value
            })
            .ToArray();

        return claims.Count > 0;
    }

    private static void NormalizeClaims(List<Claim> claims, string authTypeClaimValue)
    {
        var roleValues = claims
            .Where(static claim => claim.Type is "roles" or "role" or ClaimTypes.Role)
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var roleValue in roleValues)
        {
            if (!claims.Any(claim =>
                    claim.Type == ClaimTypes.Role &&
                    string.Equals(claim.Value, roleValue, StringComparison.OrdinalIgnoreCase)))
            {
                claims.Add(new Claim(ClaimTypes.Role, roleValue));
            }
        }

        if (!claims.Any(static claim => claim.Type == "auth_type"))
        {
            claims.Add(new Claim("auth_type", authTypeClaimValue));
        }
    }
}
