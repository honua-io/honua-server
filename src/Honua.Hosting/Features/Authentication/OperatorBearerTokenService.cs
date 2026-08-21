// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Mints and validates the console-consumable operator bearer (#2258, Option C).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the signing/validation approach of <c>PortalJwtAccessTokenService</c>
/// (HMAC-SHA256, offline signature/issuer/audience/lifetime validation) but targets
/// the general console login rather than the ArcGIS Portal OAuth2 compat surface.
/// The bearer carries the operator's admin-session claims verbatim, so the
/// request-path principal it produces — and therefore its RBAC — is identical to the
/// cookie session it was issued from.
/// </para>
/// <para>
/// The bearer is a separate, bounded credential; the upstream IdP access token is
/// never exposed. Both issuance and validation are fail-closed: when no usable
/// signing key is configured the service reports <see cref="Enabled"/> = false and
/// every operation returns no result.
/// </para>
/// </remarks>
internal sealed class OperatorBearerTokenService(IOptions<OperatorBearerOptions> options)
{
    /// <summary>
    /// Private provenance claim carrying the validated upstream identity issuer through the
    /// Honua-signed operator-bearer wrapper. The wrapper's own <c>iss</c> identifies Honua's
    /// signing authority and must never namespace the upstream subject.
    /// </summary>
    internal const string IdentityIssuerClaimType = "honua_identity_issuer";

    // JWT registered claims the handler manages itself: copying them out of the
    // source session claims (which carry the upstream id token's iss/exp/etc.) would
    // collide with the descriptor below and break issuer/lifetime validation, so they
    // are excluded on the way in and stripped again on the way out.
    private static readonly HashSet<string> ReservedClaimTypes = new(StringComparer.Ordinal)
    {
        JwtRegisteredClaimNames.Iss,
        JwtRegisteredClaimNames.Aud,
        JwtRegisteredClaimNames.Exp,
        JwtRegisteredClaimNames.Nbf,
        JwtRegisteredClaimNames.Iat,
        JwtRegisteredClaimNames.Jti,
    };

    private readonly OperatorBearerOptions _options = options.Value;

    /// <summary>Whether operator bearer issuance and validation are enabled and configured.</summary>
    public bool Enabled => _options.IsUsable;

    /// <summary>
    /// Mints an operator bearer carrying <paramref name="sessionClaims"/>, with an
    /// expiry clamped to the earlier of the configured maximum lifetime and
    /// <paramref name="sessionExpiresAt"/>. Returns <see langword="null"/> when the
    /// feature is disabled or the effective expiry is already in the past.
    /// </summary>
    public OperatorBearerIssuance? Issue(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        DateTimeOffset sessionExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(sessionClaims);

        if (!Enabled || sessionClaims.Count == 0)
        {
            return null;
        }

        if (!AdminAuthClaimsProjector.TryProjectValidatedOperatorBearerClaims(
                sessionClaims,
                out var normalizedClaims,
                out _))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var ceiling = now.AddMinutes(_options.ResolveMaxLifetimeMinutes());
        var expiresAt = sessionExpiresAt < ceiling ? sessionExpiresAt : ceiling;
        if (expiresAt <= now)
        {
            return null;
        }

        var token = SignJwt(normalizedClaims, now, expiresAt);
        return new OperatorBearerIssuance(token, expiresAt);
    }

    /// <summary>
    /// Validates an operator bearer's signature, issuer, audience and lifetime
    /// offline and returns the projected operator session claims, or
    /// <see langword="null"/> when the feature is disabled or the token is
    /// invalid/expired.
    /// </summary>
    public async Task<IReadOnlyList<AdminAuthSessionClaim>?> TryValidateAsync(string? token)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(_options.ResolveKeyBytes()),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidIssuer = _options.ResolveIssuer(),
            ValidateAudience = true,
            ValidAudience = _options.ResolveAudience(),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        if (!result.IsValid || result.ClaimsIdentity is null)
        {
            return null;
        }

        var claims = result.ClaimsIdentity.Claims
            .Where(static claim => !ReservedClaimTypes.Contains(claim.Type))
            .Select(static claim => new AdminAuthSessionClaim
            {
                Type = claim.Type,
                Value = claim.Value
            })
            .ToArray();

        if (claims.Length == 0
            || !AdminAuthClaimsProjector.TryProjectValidatedOperatorBearerClaims(
                claims,
                out var normalizedClaims,
                out var validatedProtocol))
        {
            return null;
        }

        return normalizedClaims;
    }

    /// <summary>
    /// Cheaply determines whether <paramref name="token"/> looks like an operator
    /// bearer (its unvalidated issuer matches the configured issuer) so the request
    /// pipeline can route it to the operator-bearer scheme rather than the OIDC JWT
    /// scheme. Routing only — trust is still established by <see cref="TryValidateAsync"/>.
    /// </summary>
    public bool IsOperatorBearerCandidate(string? token)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var handler = new JsonWebTokenHandler();
            if (!handler.CanReadToken(token))
            {
                return false;
            }

            var jwt = handler.ReadJsonWebToken(token);
            return string.Equals(jwt.Issuer, _options.ResolveIssuer(), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string SignJwt(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var key = new SymmetricSecurityKey(_options.ResolveKeyBytes());
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = sessionClaims
            .Where(claim => !ReservedClaimTypes.Contains(claim.Type)
                && !string.Equals(claim.Type, IdentityIssuerClaimType, StringComparison.Ordinal)
                && !string.Equals(claim.Type, IdentityProtocolProvenance.ClaimType, StringComparison.Ordinal))
            .Select(claim => new Claim(claim.Type, claim.Value))
            .ToList();

        // The session was created only after upstream token validation. Preserve that
        // validated issuer under a private, Honua-controlled claim because the wrapper JWT
        // must use its own issuer for signature validation. Never forward an upstream-provided
        // claim with our private name; synthesize exactly one value from `iss`, or none when
        // issuer provenance is absent/ambiguous so issuer-scoped consumers fail closed.
        var upstreamIssuers = sessionClaims
            .Where(static claim => string.Equals(claim.Type, JwtRegisteredClaimNames.Iss, StringComparison.Ordinal))
            .Select(static claim => claim.Value?.Trim())
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (upstreamIssuers.Length == 1)
        {
            claims.Add(new Claim(IdentityIssuerClaimType, upstreamIssuers[0]!));
        }

        // Preserve only framework-stamped protocol provenance. The generic claim copy above
        // intentionally excludes the private name so duplicate/conflicting values cannot pass
        // through the Honua-signed wrapper.
        var protocols = sessionClaims
            .Where(static claim => string.Equals(
                claim.Type,
                IdentityProtocolProvenance.ClaimType,
                StringComparison.Ordinal))
            .Select(static claim => IdentityProtocolProvenance.Normalize(claim.Value))
            .Where(IdentityProtocolProvenance.IsSupported)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (protocols.Length == 1)
        {
            claims.Add(new Claim(IdentityProtocolProvenance.ClaimType, protocols[0]!));
        }

        var now = issuedAt.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.ResolveIssuer(),
            Audience = _options.ResolveAudience(),
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            IssuedAt = now,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials,
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }
}

/// <summary>A minted operator bearer and its clamped expiry.</summary>
internal sealed record OperatorBearerIssuance(string Token, DateTimeOffset ExpiresAt);
