// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

internal static class AdminAuthClaimsProjector
{
    // The auth_type values the session writers (OIDC and SAML login) record.
    private static readonly HashSet<string> SessionAuthTypes = new(StringComparer.Ordinal) { "oidc", "saml" };

    public static ClaimsPrincipal CreatePrincipal(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        string authenticationScheme,
        string authTypeClaimValue = "oidc")
    {
        ArgumentNullException.ThrowIfNull(sessionClaims);

        var claims = sessionClaims
            .Select(static claim => new Claim(claim.Type, claim.Value))
            .ToList();

        // A session keeps the credential kind its writer recorded (OIDC or SAML login), but
        // only a value one of those writers produces; anything else falls back to the caller's.
        var storedAuthType = claims
            .LastOrDefault(static claim => string.Equals(claim.Type, "auth_type", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        NormalizeClaims(
            claims,
            storedAuthType is not null && SessionAuthTypes.Contains(storedAuthType)
                ? storedAuthType
                : authTypeClaimValue);

        var identity = new ClaimsIdentity(
            claims,
            string.IsNullOrWhiteSpace(authenticationScheme) ? "oidc" : authenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        // NormalizeClaims has replaced the authority claims with the one auth_type this
        // projector chose, so what remains is server-derived and is marked as such. A
        // session record written before the ingress sanitization existed is normalized
        // the same way, so an old record cannot carry an issuer value forward either.
        CanonicalSecurityActor.StampAuthorityClaims(identity);

        return new ClaimsPrincipal(identity);
    }

    public static bool TryProjectValidatedClaims(
        IEnumerable<Claim> sourceClaims,
        out IReadOnlyList<AdminAuthSessionClaim> claims,
        string authTypeClaimValue = "oidc")
    {
        ArgumentNullException.ThrowIfNull(sourceClaims);

        var projectedClaims = sourceClaims.ToList();

        if (projectedClaims.Count == 0)
        {
            claims = [];
            return false;
        }

        NormalizeClaims(projectedClaims, authTypeClaimValue);

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
        // A session record holds only (type, value) pairs, so in-memory framework
        // provenance cannot travel with it. Drop every framework-owned authority claim
        // the source carried — an identity provider's ID token, a SAML assertion, or an
        // older session record — and let the caller's own auth_type be the only one that
        // reaches the session. Roles are untouched: mapping provider claims to roles is
        // the operator's supported way to grant authority.
        claims.RemoveAll(static claim =>
            CanonicalSecurityActor.IsFrameworkAuthorityClaimType(claim.Type));

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

        claims.Add(new Claim("auth_type", authTypeClaimValue));
    }
}
