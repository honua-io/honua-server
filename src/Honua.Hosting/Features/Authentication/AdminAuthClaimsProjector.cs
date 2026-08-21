// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Security;

namespace Honua.Infrastructure.Authentication;

internal static class AdminAuthClaimsProjector
{
    private const string ApiKeyIdClaimType = "api_key_id";
    private const string IdentityIssuerClaimType = "honua_identity_issuer";

    public static ClaimsPrincipal CreatePrincipal(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        string authenticationScheme,
        string? validatedProtocol = null)
    {
        ArgumentNullException.ThrowIfNull(sessionClaims);

        var claims = sessionClaims
            .Select(static claim => new Claim(claim.Type, claim.Value))
            .ToList();

        var protocol = IdentityProtocolProvenance.IsSupported(
            IdentityProtocolProvenance.Normalize(validatedProtocol))
            ? IdentityProtocolProvenance.Normalize(validatedProtocol)
            : ResolvePrivateProtocol(claims);
        NormalizeClaims(
            claims,
            protocol,
            preserveIdentityIssuer: string.Equals(
                authenticationScheme,
                FrameworkAuthenticationIdentity.OperatorBearerAuthenticationType,
                StringComparison.Ordinal));

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

        NormalizeClaims(projectedClaims, IdentityProtocolProvenance.Normalize(validatedProtocol));

        claims = ToSessionClaims(projectedClaims);
        return claims.Count > 0;
    }

    /// <summary>
    /// Reprojects a persisted admin session using its framework-owned provider key. This is
    /// intentionally the only legacy-session upgrade seam: raw <c>auth_type</c> and private
    /// lookalike claims stored before protocol provenance existed are never promoted.
    /// </summary>
    public static bool TryProjectPersistedSessionClaims(
        IReadOnlyList<AdminAuthSessionClaim> sourceClaims,
        string? providerKey,
        out IReadOnlyList<AdminAuthSessionClaim> claims,
        out string validatedProtocol)
    {
        ArgumentNullException.ThrowIfNull(sourceClaims);

        if (!TryResolvePersistedSessionProtocol(providerKey, out validatedProtocol))
        {
            claims = [];
            return false;
        }

        var projectedClaims = sourceClaims
            .Select(static claim => new Claim(claim.Type, claim.Value))
            .ToList();
        if (projectedClaims.Count == 0)
        {
            claims = [];
            return false;
        }

        NormalizeClaims(projectedClaims, validatedProtocol);
        claims = ToSessionClaims(projectedClaims);
        return claims.Count > 0;
    }

    /// <summary>
    /// Normalizes claims from a signature-validated Honua operator bearer. Only the private
    /// protocol claim is trusted at this boundary; display <c>auth_type</c> and API-key claims
    /// can never change the durable actor kind.
    /// </summary>
    public static bool TryProjectValidatedOperatorBearerClaims(
        IReadOnlyList<AdminAuthSessionClaim> sourceClaims,
        out IReadOnlyList<AdminAuthSessionClaim> claims,
        out string validatedProtocol)
    {
        ArgumentNullException.ThrowIfNull(sourceClaims);

        var sourceProtocols = sourceClaims
            .Where(static claim => string.Equals(
                claim.Type,
                IdentityProtocolProvenance.ClaimType,
                StringComparison.Ordinal))
            .Select(static claim => IdentityProtocolProvenance.Normalize(claim.Value))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (sourceProtocols.Length != 1 || !IdentityProtocolProvenance.IsSupported(sourceProtocols[0]))
        {
            claims = [];
            validatedProtocol = string.Empty;
            return false;
        }

        validatedProtocol = sourceProtocols[0]!;
        var projectedClaims = sourceClaims
            .Select(static claim => new Claim(claim.Type, claim.Value))
            .ToList();
        NormalizeClaims(projectedClaims, validatedProtocol, preserveIdentityIssuer: true);
        claims = ToSessionClaims(projectedClaims);
        return claims.Count > 0;
    }

    private static bool TryResolvePersistedSessionProtocol(
        string? providerKey,
        out string validatedProtocol)
    {
        var normalizedProviderKey = providerKey?.Trim();
        if (string.IsNullOrEmpty(normalizedProviderKey))
        {
            validatedProtocol = string.Empty;
            return false;
        }

        validatedProtocol = string.Equals(
            normalizedProviderKey,
            IdentityProtocolProvenance.Saml,
            StringComparison.OrdinalIgnoreCase)
            ? IdentityProtocolProvenance.Saml
            : IdentityProtocolProvenance.Oidc;
        return true;
    }

    private static AdminAuthSessionClaim[] ToSessionClaims(
        IEnumerable<Claim> projectedClaims)
    {
        return projectedClaims
            .GroupBy(static claim => $"{claim.Type}\u001f{claim.Value}", StringComparer.Ordinal)
            .Select(static group => new AdminAuthSessionClaim
            {
                Type = group.First().Type,
                Value = group.First().Value
            })
            .ToArray();
    }

    private static void NormalizeClaims(
        List<Claim> claims,
        string? validatedProtocol,
        bool preserveIdentityIssuer = false)
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

        var protocol = IdentityProtocolProvenance.Normalize(validatedProtocol);

        // auth_type is retained as a compatibility/display claim, but never trusted as the
        // durable subject protocol. Both it and the private provenance claim are normalized at
        // validation boundaries so an upstream auth_type=saml cannot cross into the SAML
        // issuer-optional namespace after OIDC validation.
        claims.RemoveAll(claim =>
            claim.Type is "auth_type" or IdentityProtocolProvenance.ClaimType or ApiKeyIdClaimType
            || string.Equals(
                claim.Type,
                FrameworkAuthenticationIdentity.CredentialKindClaimType,
                StringComparison.Ordinal)
            || (!preserveIdentityIssuer && string.Equals(
                claim.Type,
                IdentityIssuerClaimType,
                StringComparison.Ordinal)));
        if (IdentityProtocolProvenance.IsSupported(protocol))
        {
            claims.Add(new Claim("auth_type", protocol!));
            claims.Add(new Claim(IdentityProtocolProvenance.ClaimType, protocol!));
        }
    }

    private static string? ResolvePrivateProtocol(IEnumerable<Claim> claims)
    {
        var protocols = claims
            .Where(static claim => string.Equals(
                claim.Type,
                IdentityProtocolProvenance.ClaimType,
                StringComparison.Ordinal))
            .Select(static claim => IdentityProtocolProvenance.Normalize(claim.Value))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return protocols.Length == 1 && IdentityProtocolProvenance.IsSupported(protocols[0])
            ? protocols[0]
            : null;
    }
}
