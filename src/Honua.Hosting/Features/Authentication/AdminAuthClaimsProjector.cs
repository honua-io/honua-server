// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Security;

namespace Honua.Infrastructure.Authentication;

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

        NormalizeClaims(claims, authTypeClaimValue, overrideProtocol: false);

        var identity = new ClaimsIdentity(
            claims,
            string.IsNullOrWhiteSpace(authenticationScheme) ? "oidc" : authenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    public static bool TryProjectValidatedClaims(
        IEnumerable<Claim> sourceClaims,
        out IReadOnlyList<AdminAuthSessionClaim> claims,
        string validatedProtocol = IdentityProtocolProvenance.Oidc)
    {
        ArgumentNullException.ThrowIfNull(sourceClaims);

        var projectedClaims = sourceClaims.ToList();

        if (projectedClaims.Count == 0)
        {
            claims = [];
            return false;
        }

        if (!IdentityProtocolProvenance.IsSupported(
                IdentityProtocolProvenance.Normalize(validatedProtocol)))
        {
            claims = [];
            return false;
        }

        NormalizeClaims(projectedClaims, validatedProtocol, overrideProtocol: true);

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

    private static void NormalizeClaims(
        List<Claim> claims,
        string authTypeClaimValue,
        bool overrideProtocol)
    {
        var roleValues = claims
            .Where(static claim => claim.Type is "roles" or "role" or ClaimTypes.Role)
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Not converted to LINQ: each iteration mutates `claims` (the same list the predicate
        // queries), so a Where/Select projection over roleValues would read as pure but isn't.
        foreach (var roleValue in (roleValues).Where(roleValue => !claims.Any(claim => claim.Type == ClaimTypes.Role && string.Equals(claim.Value, roleValue, StringComparison.OrdinalIgnoreCase))))
        {
            claims.Add(new Claim(ClaimTypes.Role, roleValue));
        }

        var normalizedFallback = IdentityProtocolProvenance.Normalize(authTypeClaimValue);
        var existingProtocols = claims
            .Where(static claim => claim.Type is "auth_type" or IdentityProtocolProvenance.ClaimType)
            .Select(static claim => IdentityProtocolProvenance.Normalize(claim.Value))
            .Where(IdentityProtocolProvenance.IsSupported)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        var protocol = overrideProtocol
            ? normalizedFallback
            : existingProtocols.Length == 1
                ? existingProtocols[0]
                : IdentityProtocolProvenance.IsSupported(normalizedFallback)
                    ? normalizedFallback
                    : null;

        // auth_type is retained as a compatibility/display claim, but never trusted as the
        // durable subject protocol. Both it and the private provenance claim are normalized at
        // validation boundaries so an upstream auth_type=saml cannot cross into the SAML
        // issuer-optional namespace after OIDC validation.
        claims.RemoveAll(static claim => claim.Type is "auth_type" or IdentityProtocolProvenance.ClaimType);
        if (protocol is not null)
        {
            claims.Add(new Claim("auth_type", protocol));
            claims.Add(new Claim(IdentityProtocolProvenance.ClaimType, protocol));
        }
        else if (!string.IsNullOrWhiteSpace(authTypeClaimValue))
        {
            claims.Add(new Claim("auth_type", authTypeClaimValue.Trim().ToLowerInvariant()));
        }
    }
}
