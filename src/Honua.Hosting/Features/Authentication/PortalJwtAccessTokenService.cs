// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Optional JWT access-token format for the OAuth2 surface (ADR-0054, #1890).
/// </summary>
/// <remarks>
/// <para>
/// Honua's baseline (ADR-0049/0053) is a single opaque, cache-backed token with
/// cache-eviction revocation and one request-path validator. This service is the
/// strictly opt-in JWT alternative: it mints an HMAC-SHA256-signed JWT carrying the
/// principal, roles, binding and a <c>jti</c>, and <em>also</em> records that
/// <c>jti</c> in the shared portal-token cache via <see cref="IPortalTokenIssuer"/>.
/// </para>
/// <para>
/// Recording the <c>jti</c> in the same cache keeps the two ADR-0053 §4 invariants
/// that JWT would otherwise reopen: (1) revocation stays synchronous — evicting the
/// cache entry stops the token, exactly as for opaque tokens — and (2) the request
/// path still has a single validator (<c>PortalTokenAuthenticationHandler</c> over
/// the cache), so a JWT validates on <c>/rest/services/*</c> with no second
/// validation path. The JWT's signature gives operators stateless validation at the
/// introspection endpoint without the request path forking.
/// </para>
/// </remarks>
internal sealed class PortalJwtAccessTokenService(
    IPortalTokenIssuer tokenIssuer,
    IOptions<PortalTokenAuthenticationOptions> tokenOptions)
{
    internal const int MinimumSigningKeyBytes = 32;

    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer;
    private readonly PortalTokenAuthenticationOptions _tokenOptions = tokenOptions.Value;

    /// <summary>Whether minting JWT access tokens is enabled and correctly configured.</summary>
    public bool JwtEnabled => _tokenOptions.OAuth2.Jwt.EnableJwtAccessTokens && HasUsableKey(_tokenOptions.OAuth2.Jwt);

    /// <summary>
    /// Mints a JWT access token for the supplied principal. The opaque reference the
    /// JWT's <c>jti</c> points at is recorded through <see cref="IPortalTokenIssuer"/>
    /// so cache-eviction revocation and the single request-path validator are
    /// preserved. Returns the signed compact JWT and its expiry.
    /// </summary>
    public async Task<PortalTokenIssuance> IssueAsync(
        PortalTokenIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The jti IS the opaque reference: persisting it through the existing issuer
        // means the request path validates the JWT exactly like an opaque token
        // (it looks the jti up in the cache) — no second validator (ADR-0053 §4).
        var reference = await _tokenIssuer.IssueAsync(request, cancellationToken).ConfigureAwait(false);

        var jwt = SignJwt(request, reference.Token, reference.ExpiresAt);
        return new PortalTokenIssuance(jwt, reference.ExpiresAt);
    }

    private string SignJwt(PortalTokenIssueRequest request, string jti, DateTimeOffset expiresAt)
    {
        var options = _tokenOptions.OAuth2.Jwt;
        var key = new SymmetricSecurityKey(ResolveKeyBytes(options));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, request.PrincipalId),
            // jti is the opaque cache reference: the request-path handler resolves it
            // exactly like a raw opaque token, so revocation stays cache-eviction.
            new(JwtRegisteredClaimNames.Jti, jti),
            new("auth_type", PortalTokenIssuer.AuthTypeClaimValue),
        };

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            claims.Add(new Claim("display_name", request.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(request.TenantId))
        {
            claims.Add(new Claim(PortalTokenIssuer.TenantClaimType, request.TenantId!));
        }

        foreach (var role in request.Roles.Where(role => !string.IsNullOrWhiteSpace(role)))
        {
            claims.Add(new Claim("role", role));
        }

        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = string.IsNullOrWhiteSpace(options.Issuer) ? PortalOAuthJwtOptions.DefaultIssuer : options.Issuer,
            Audience = string.IsNullOrWhiteSpace(options.Audience) ? null : options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            IssuedAt = now,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials,
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Validates a JWT's signature and lifetime offline (stateless). Returns the
    /// embedded <c>jti</c> (the opaque cache reference) when the signature, issuer,
    /// audience and lifetime check out, otherwise <see langword="null"/>. The caller
    /// then confirms the <c>jti</c> is still present (not revoked) in the cache.
    /// </summary>
    public async Task<string?> TryReadReferenceAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var options = _tokenOptions.OAuth2.Jwt;
        if (!HasUsableKey(options))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(ResolveKeyBytes(options)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = !string.IsNullOrWhiteSpace(options.Issuer),
            ValidIssuer = options.Issuer,
            ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience),
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return null;
        }

        return result.ClaimsIdentity?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
    }

    private static bool HasUsableKey(PortalOAuthJwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            return false;
        }

        return ResolveKeyBytes(options).Length >= MinimumSigningKeyBytes;
    }

    private static byte[] ResolveKeyBytes(PortalOAuthJwtOptions options)
    {
        var resolved = SecretResolver.Resolve(options.SigningKey) ?? string.Empty;
        return Encoding.UTF8.GetBytes(resolved);
    }
}

/// <summary>
/// Minimal <c>env:</c>-indirection secret resolver mirroring the OIDC secret
/// handling: a value of the form <c>env:NAME</c> resolves to the environment
/// variable <c>NAME</c>; any other value is used verbatim.
/// </summary>
internal static class SecretResolver
{
    private const string EnvPrefix = "env:";

    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (value.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = value[EnvPrefix.Length..].Trim();
            return Environment.GetEnvironmentVariable(name);
        }

        return value;
    }
}
