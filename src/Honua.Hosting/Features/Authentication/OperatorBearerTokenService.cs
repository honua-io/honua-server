// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Security;
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
        SessionBindingClaimType,
        AdminPasswordVersionClaimType,
        SubjectIssuerClaimType,
        OwnerIdentityScope.SubjectIssuerClaim,
    };

    // Present on bearers minted from a browser session; the request path refuses the bearer once that session ends.
    internal const string SessionBindingClaimType = "honua_session_binding";

    // Present on bearers minted from the administrator password; the request path refuses the
    // bearer once the password it was minted from is rotated (the marker portal tokens minted from the password use).
    internal const string AdminPasswordVersionClaimType = "honua_admin_password_version";

    // Preserves the upstream IdP issuer across the operator-bearer issuer rewrite so managed-user
    // revalidation still resolves issuer-scoped SCIM/OIDC accounts.
    internal const string SubjectIssuerClaimType = "honua_subject_issuer";

    private readonly OperatorBearerOptions _options = options.Value;

    public bool Enabled => _options.IsUsable;

    internal string ValidatedIssuer => _options.ResolveIssuer();

    public OperatorBearerIssuance? Issue(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        DateTimeOffset sessionExpiresAt,
        OperatorBearerBinding binding = default)
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

        var token = SignJwt(CreateClaims(sessionClaims, binding, _options.ResolveIssuer()), now, expiresAt);
        return new OperatorBearerIssuance(token, expiresAt);
    }

    public async Task<OperatorBearerValidation?> TryValidateAsync(string? token)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, CreateValidationParameters())
            .ConfigureAwait(false);
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

        if (claims.Length == 0)
        {
            return null;
        }

        return new OperatorBearerValidation(
            claims,
            new OperatorBearerBinding(
                NullIfEmpty(result.ClaimsIdentity.FindFirst(SessionBindingClaimType)?.Value),
                NullIfEmpty(result.ClaimsIdentity.FindFirst(AdminPasswordVersionClaimType)?.Value)),
            NullIfEmpty(result.ClaimsIdentity.FindFirst(SubjectIssuerClaimType)?.Value));
    }

    private TokenValidationParameters CreateValidationParameters() =>
        new()
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static List<Claim> CreateClaims(
        IReadOnlyList<AdminAuthSessionClaim> sessionClaims,
        OperatorBearerBinding binding,
        string validatedIssuer)
    {
        var upstreamIssuer = sessionClaims
            .FirstOrDefault(static claim => string.Equals(claim.Type, JwtRegisteredClaimNames.Iss, StringComparison.Ordinal))
            ?.Value?.Trim();

        var claims = sessionClaims
            .Where(claim => !ReservedClaimTypes.Contains(claim.Type))
            .Select(claim => new Claim(claim.Type, claim.Value))
            .ToList();
        if (!string.IsNullOrEmpty(upstreamIssuer)
            && !string.Equals(upstreamIssuer, validatedIssuer, StringComparison.Ordinal))
        {
            claims.Add(new Claim(SubjectIssuerClaimType, upstreamIssuer));
        }

        if (!string.IsNullOrEmpty(binding.Session))
        {
            claims.Add(new Claim(SessionBindingClaimType, binding.Session));
        }

        if (!string.IsNullOrEmpty(binding.AdminPasswordVersion))
        {
            claims.Add(new Claim(AdminPasswordVersionClaimType, binding.AdminPasswordVersion));
        }

        return claims;
    }

    private string SignJwt(
        List<Claim> claims,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var key = new SymmetricSecurityKey(_options.ResolveKeyBytes());
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

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

internal sealed record OperatorBearerValidation(
    IReadOnlyList<AdminAuthSessionClaim> Claims,
    OperatorBearerBinding Binding,
    string? SubjectIssuer = null);

// What a bearer is revoked with: the browser session it was minted from, or the administrator
// password version it was minted from.
internal readonly record struct OperatorBearerBinding(string? Session = null, string? AdminPasswordVersion = null);
