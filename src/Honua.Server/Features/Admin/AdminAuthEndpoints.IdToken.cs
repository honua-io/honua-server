// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Features.Admin;

internal static partial class AdminAuthEndpoints
{
    private enum DiscoveryNetworkPolicy
    {
        PublicOnly,
        AllowPrivate
    }

    private sealed class AdminIdTokenValidationContext
    {
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required AdminAuthOidcDiscoveryDocument Discovery { get; init; }
        public required OidcProviderRegistration Provider { get; init; }
        public required OidcAuthenticationOptions OidcOptions { get; init; }
        public string? IdToken { get; init; }
        public string? ExpectedNonce { get; init; }
        public required DiscoveryNetworkPolicy NetworkPolicy { get; init; }
    }

    private static DateTimeOffset? TryGetIdTokenExpiry(IReadOnlyList<Claim> claims)
    {
        foreach (var claim in claims)
        {
            if (!string.Equals(claim.Type, "exp", StringComparison.Ordinal))
            {
                continue;
            }

            if (long.TryParse(
                    claim.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var epoch))
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
        }

        return null;
    }

    private static DateTimeOffset ResolveSessionExpiration(
        int expiresInSeconds,
        IReadOnlyList<Claim> validatedClaims,
        DateTimeOffset now)
    {
        var effectiveExpiresIn = expiresInSeconds > 0 ? expiresInSeconds : 300;
        var sessionExpiresAt = now.AddSeconds(effectiveExpiresIn);
        var idTokenExpiry = TryGetIdTokenExpiry(validatedClaims);
        if (idTokenExpiry.HasValue && idTokenExpiry.Value < sessionExpiresAt)
        {
            sessionExpiresAt = idTokenExpiry.Value;
        }

        return sessionExpiresAt;
    }

    private static async Task<IReadOnlyList<Claim>?> ValidateAdminSessionIdTokenAsync(
        AdminIdTokenValidationContext validation,
        CancellationToken cancellationToken)
    {
        if (!HasRequiredIdTokenInputs(validation))
        {
            return null;
        }

        var discoveryIssuer = validation.Discovery.Issuer!.Trim();
        if (!IsAllowedDiscoveryIssuer(discoveryIssuer, validation.Provider, validation.OidcOptions))
        {
            return null;
        }

        var signingKeys = await LoadValidatedSigningKeysAsync(validation, cancellationToken).ConfigureAwait(false);
        if (signingKeys.Count == 0)
        {
            return null;
        }

        return ValidateSignedIdTokenClaims(validation, discoveryIssuer, signingKeys);
    }

    private static bool HasRequiredIdTokenInputs(AdminIdTokenValidationContext validation)
        => !string.IsNullOrWhiteSpace(validation.IdToken)
            && !string.IsNullOrWhiteSpace(validation.ExpectedNonce)
            && !string.IsNullOrWhiteSpace(validation.Discovery.Issuer)
            && !string.IsNullOrWhiteSpace(validation.Discovery.JwksUri)
            && !string.IsNullOrWhiteSpace(validation.Provider.ClientId);

    private readonly record struct AdminDiscoveryPolicyOptions(
        OidcAuthenticationOptions OidcOptions,
        DiscoveryNetworkPolicy NetworkPolicy);

    private static async Task<IReadOnlyList<SecurityKey>> LoadValidatedSigningKeysAsync(
        AdminIdTokenValidationContext validation,
        CancellationToken cancellationToken)
    {
        var policy = BuildDiscoveryEndpointPolicy(
            validation.Discovery.JwksUri,
            validation.Provider,
            new AdminDiscoveryPolicyOptions(validation.OidcOptions, validation.NetworkPolicy));
        if (!await PortalOAuthBroker.IsValidDiscoveryEndpointAsync(
                validation.Discovery.JwksUri!,
                policy,
                cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return await GetOidcSigningKeysAsync(
            validation.HttpClientFactory,
            validation.Discovery.JwksUri!,
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsSameAuthorityOrigin(string? endpoint, string? authority)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            && Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
            && string.Equals(endpointUri.Scheme, authorityUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(endpointUri.Host, authorityUri.Host, StringComparison.OrdinalIgnoreCase)
            && endpointUri.Port == authorityUri.Port;

    private static DiscoveryEndpointPolicy BuildDiscoveryEndpointPolicy(
        string? endpoint,
        OidcProviderRegistration provider,
        AdminDiscoveryPolicyOptions options)
    {
        var policy = options.OidcOptions.RequireHttps
            ? DiscoveryEndpointPolicy.Strict
            : DiscoveryEndpointPolicy.AllowHttp;
        if (options.NetworkPolicy == DiscoveryNetworkPolicy.AllowPrivate
            || IsSameAuthorityOrigin(endpoint, provider.Authority))
        {
            policy |= DiscoveryEndpointPolicy.AllowPrivateNetworks;
        }

        return policy;
    }

    private static Claim[]? ValidateSignedIdTokenClaims(
        AdminIdTokenValidationContext validation,
        string discoveryIssuer,
        IReadOnlyList<SecurityKey> signingKeys)
    {
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(validation.IdToken))
        {
            return null;
        }

        try
        {
            var principal = handler.ValidateToken(
                validation.IdToken,
                BuildIdTokenValidationParameters(discoveryIssuer, validation.Provider.ClientId!, signingKeys),
                out _);

            var claims = principal.Claims.ToArray();
            var tokenNonce = claims
                .FirstOrDefault(static claim => string.Equals(claim.Type, JwtRegisteredClaimNames.Nonce, StringComparison.Ordinal))
                ?.Value;
            return PortalOAuthBroker.CryptographicEquals(validation.ExpectedNonce!, tokenNonce) ? claims : null;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    private static TokenValidationParameters BuildIdTokenValidationParameters(
        string discoveryIssuer,
        string clientId,
        IReadOnlyList<SecurityKey> signingKeys) => new()
        {
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidAlgorithms = AllowedIdTokenAlgorithms,
            ValidateIssuer = true,
            ValidIssuer = discoveryIssuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

    internal static bool IsAllowedDiscoveryIssuer(
        string discoveryIssuer,
        OidcProviderRegistration provider,
        OidcAuthenticationOptions oidcOptions)
    {
        var normalizedDiscovery = discoveryIssuer.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedDiscovery))
        {
            return false;
        }

        var baseParameters = OidcTokenValidationParametersFactory.Build(oidcOptions).Parameters;
        var narrowed = OidcTokenValidationParametersFactory.NarrowToProvider(baseParameters, provider, oidcOptions);
        var allowedIssuers = (narrowed.ValidIssuers ?? [])
            .Where(static issuer => !string.IsNullOrWhiteSpace(issuer));
        if (allowedIssuers.Any(candidate =>
                string.Equals(candidate.Trim().TrimEnd('/'), normalizedDiscovery, StringComparison.Ordinal)))
        {
            return true;
        }

        return OidcProviderDiscoveryProbe.IsExpectedIssuer(provider.Authority, provider.Type, discoveryIssuer);
    }

    private static DiscoveryNetworkPolicy ResolveDiscoveryNetworkPolicy(HttpContext context)
        => context.RequestServices.GetService<IHostEnvironment>() is { } environment
            && (environment.IsDevelopment() || environment.IsEnvironment("Test"))
            ? DiscoveryNetworkPolicy.AllowPrivate
            : DiscoveryNetworkPolicy.PublicOnly;

    private static async Task<IReadOnlyList<SecurityKey>> GetOidcSigningKeysAsync(
        IHttpClientFactory httpClientFactory,
        string jwksUri,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AdminAuthHttpClient);
        using var response = await client
            .GetAsync(jwksUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var body = await OidcBackchannelHttpHandler
            .ReadBoundedBytesAsync(response.Content, OidcBackchannelHttpHandler.MaxMetadataDocumentBytes, cancellationToken)
            .ConfigureAwait(false);
        var jwksJson = Encoding.UTF8.GetString(body);
        if (string.IsNullOrWhiteSpace(jwksJson))
        {
            return [];
        }

        var keySet = new JsonWebKeySet(jwksJson);
        return keySet.GetSigningKeys().ToArray();
    }
}
