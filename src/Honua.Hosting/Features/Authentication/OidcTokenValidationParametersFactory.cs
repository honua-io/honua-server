// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Honua.Infrastructure.Authentication;

// A per-issuer audience check rejects a token whose audience belongs to a different configured provider.
internal static class OidcTokenValidationParametersFactory
{
    public readonly record struct BuildResult(
        TokenValidationParameters Parameters,
        IReadOnlyList<string> Authorities);

    public static BuildResult Build(
        OidcAuthenticationOptions options,
        IOidcRuntimeProviderSource? runtimeProviders = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = options.TokenValidation.ValidateIssuer,
            ValidateAudience = options.TokenValidation.ValidateAudience,
            ValidateLifetime = options.TokenValidation.ValidateLifetime,
            ValidateIssuerSigningKey = options.TokenValidation.ValidateIssuerSigningKey,
            ClockSkew = options.TokenValidation.ClockSkew,
            NameClaimType = options.ClaimsMapping.NameClaimType,
            RoleClaimType = options.ClaimsMapping.RoleClaimType,
        };

        var issuers = new HashSet<string>(StringComparer.Ordinal);
        var audiences = new HashSet<string>(StringComparer.Ordinal);
        var audiencesByIssuer = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var authorities = new List<string>();

        foreach (var provider in OidcProviderCatalog.GetProviders(options, runtimeProviders))
        {
            if (!provider.IsValid)
            {
                continue;
            }

            AddProviderBinding(provider, options, issuers, audiences, audiencesByIssuer, authorities);
        }

        foreach (var issuer in options.TokenValidation.ValidIssuers)
        {
            if (!string.IsNullOrWhiteSpace(issuer))
            {
                issuers.Add(issuer);
            }
        }

        foreach (var audience in options.TokenValidation.ValidAudiences)
        {
            if (!string.IsNullOrWhiteSpace(audience))
            {
                audiences.Add(audience);
            }
        }

        parameters.ValidIssuers = issuers.ToArray();
        parameters.ValidAudiences = audiences.ToArray();
        if (options.TokenValidation.ValidateAudience && audiencesByIssuer.Count > 0)
        {
            var pairing = audiencesByIssuer;
            parameters.AudienceValidator = (tokenAudiences, securityToken, validationParameters) =>
            {
                var issuer = securityToken?.Issuer;
                if (!string.IsNullOrWhiteSpace(issuer) && pairing.TryGetValue(issuer, out var allowedForIssuer))
                {
                    return tokenAudiences is not null && tokenAudiences.Any(allowedForIssuer.Contains);
                }

                var fallback = validationParameters.ValidAudiences;
                return tokenAudiences is not null && fallback is not null &&
                    tokenAudiences.Any(audience =>
                        fallback.Any(valid => string.Equals(valid, audience, StringComparison.Ordinal)));
            };
        }

        var staticSigningKey = options.TokenValidation.SymmetricSigningKey;
        if (!string.IsNullOrWhiteSpace(staticSigningKey))
        {
            parameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(staticSigningKey));
        }

        return new BuildResult(parameters, authorities);
    }

    // Parameters are limited to the provider that started the broker flow so a token from another configured authority cannot be substituted.
    public static TokenValidationParameters NarrowToProvider(
        TokenValidationParameters parameters,
        OidcProviderRegistration provider,
        OidcAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);

        var issuers = new HashSet<string>(StringComparer.Ordinal);
        var audiences = new HashSet<string>(StringComparer.Ordinal);
        var pairing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        AddProviderBinding(provider, options, issuers, audiences, pairing, authorities: []);

        var narrowed = parameters.Clone();
        narrowed.ValidIssuer = null;
        narrowed.ValidIssuers = issuers.ToArray();
        narrowed.ValidAudiences = audiences.ToArray();
        narrowed.AudienceValidator = (tokenAudiences, _, _) =>
            tokenAudiences is not null && tokenAudiences.Any(audience =>
                audiences.Contains(audience));
        return narrowed;
    }

    private static void AddProviderBinding(
        OidcProviderRegistration provider,
        OidcAuthenticationOptions options,
        HashSet<string> issuers,
        HashSet<string> audiences,
        Dictionary<string, HashSet<string>> audiencesByIssuer,
        List<string> authorities)
    {
        if (string.IsNullOrWhiteSpace(provider.Authority) || string.IsNullOrWhiteSpace(provider.ClientId))
        {
            return;
        }

        var authority = provider.Authority.Trim();
        var metadataAuthority = authority.TrimEnd('/');
        if (!authorities.Contains(metadataAuthority, StringComparer.OrdinalIgnoreCase))
        {
            authorities.Add(metadataAuthority);
        }

        var providerIssuers = new List<string> { authority };
        var providerAudiences = new List<string> { provider.ClientId.Trim() };

        if (string.Equals(provider.Type, "AzureAd", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.AzureAd?.TenantId))
        {
            providerIssuers.Add($"https://sts.windows.net/{options.AzureAd.TenantId}/");
            providerAudiences.Add($"api://{provider.ClientId.Trim()}");
        }

        if (string.Equals(provider.Type, "Auth0", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.Auth0?.Audience))
        {
            providerAudiences.Add(options.Auth0.Audience);
        }

        foreach (var issuer in providerIssuers)
        {
            issuers.Add(issuer);
            if (!audiencesByIssuer.TryGetValue(issuer, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                audiencesByIssuer[issuer] = set;
            }

            foreach (var audience in providerAudiences)
            {
                set.Add(audience);
                audiences.Add(audience);
            }
        }
    }
}
