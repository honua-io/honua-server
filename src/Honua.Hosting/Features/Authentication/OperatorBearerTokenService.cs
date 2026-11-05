// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

// Claims are copied verbatim so bearer authorization matches the cookie session, and a missing signing key leaves the feature off.
internal sealed class OperatorBearerTokenService(IOptions<OperatorBearerOptions> options)
{
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

    public bool Enabled => _options.IsUsable;

    internal string ValidatedIssuer => _options.ResolveIssuer();

    public OperatorBearerIssuance? Issue(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        DateTimeOffset sessionExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(sessionClaims);

        if (!Enabled || sessionClaims.Count == 0)
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

        var token = SignJwt(sessionClaims, now, expiresAt);
        return new OperatorBearerIssuance(token, expiresAt);
    }

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

        return claims.Length == 0 ? null : claims;
    }

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
            .Where(claim => !ReservedClaimTypes.Contains(claim.Type))
            .Select(claim => new Claim(claim.Type, claim.Value))
            .ToList();

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

internal sealed record OperatorBearerIssuance(string Token, DateTimeOffset ExpiresAt);
