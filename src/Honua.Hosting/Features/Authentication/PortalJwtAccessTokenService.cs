// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

internal sealed class PortalJwtAccessTokenService(
    IPortalTokenIssuer tokenIssuer,
    IOptions<PortalTokenAuthenticationOptions> tokenOptions)
{
    internal const int MinimumSigningKeyBytes = 32;

    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer;
    private readonly PortalTokenAuthenticationOptions _tokenOptions = tokenOptions.Value;

    public bool JwtEnabled => _tokenOptions.OAuth2.Jwt.EnableJwtAccessTokens && HasUsableKey(_tokenOptions.OAuth2.Jwt);

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

        if (request.IsClientCredentials)
        {
            claims.Add(new Claim("honua_auth_flow", "client_credentials"));
        }

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

// A value prefixed with env: is read from that environment variable; any other value is the secret itself.
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
