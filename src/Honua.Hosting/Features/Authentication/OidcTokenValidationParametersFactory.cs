// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Builds <see cref="TokenValidationParameters"/> from
/// <see cref="OidcAuthenticationOptions"/> so server-side OIDC token validation
/// (the named-user portal credential verifier, #1370) uses the same issuer,
/// audience, signing-key, and claims-mapping rules as the interactive JwtBearer
/// scheme configured in <see cref="OidcAuthenticationExtensions"/>.
/// </summary>
/// <remarks>
/// Centralizing the rules keeps the portal verifier's decision identical to a
/// direct OIDC Bearer session: the issuers/audiences derived here mirror
/// <c>ConfigureJwtBearerAuthentication</c>. The authority returned alongside the
/// parameters is the metadata endpoint the caller should configure on a
/// <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;</c> for signing-key
/// discovery when no static symmetric key is set.
/// </remarks>
internal static class OidcTokenValidationParametersFactory
{
    /// <summary>
    /// Builds validation parameters and resolves the metadata authority.
    /// </summary>
    /// <param name="options">The OIDC authentication options.</param>
    /// <returns>The validation parameters and the resolved authority (or
    /// <see langword="null"/> when a static symmetric signing key is configured
    /// and metadata discovery is not required).</returns>
    public static (TokenValidationParameters Parameters, string? Authority) Build(OidcAuthenticationOptions options)
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

        parameters.ValidIssuers = BuildIssuers(options);
        parameters.ValidAudiences = BuildAudiences(options);

        var staticSigningKey = options.TokenValidation.SymmetricSigningKey;
        if (!string.IsNullOrWhiteSpace(staticSigningKey))
        {
            parameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(staticSigningKey));
            return (parameters, null);
        }

        return (parameters, ResolveAuthority(options));
    }

    private static string[] BuildIssuers(OidcAuthenticationOptions options)
    {
        var issuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.AzureAd?.IsValid == true)
        {
            var tenantId = options.AzureAd.TenantId!;
            issuers.Add($"{options.AzureAd.Instance}{tenantId}/v2.0");
            issuers.Add($"https://sts.windows.net/{tenantId}/");
        }

        if (options.Google?.IsValid == true)
        {
            issuers.Add("https://accounts.google.com");
        }

        if (options.Generic?.IsValid == true && !string.IsNullOrEmpty(options.Generic.Authority))
        {
            issuers.Add(options.Generic.Authority);
        }

        if (options.Okta?.IsValid == true)
        {
            issuers.Add(options.Okta.GetAuthority());
        }

        if (options.Auth0?.IsValid == true)
        {
            issuers.Add(options.Auth0.GetAuthority());
        }

        foreach (var issuer in options.TokenValidation.ValidIssuers)
        {
            issuers.Add(issuer);
        }

        return [.. issuers];
    }

    private static string[] BuildAudiences(OidcAuthenticationOptions options)
    {
        var audiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.AzureAd?.IsValid == true)
        {
            var clientId = options.AzureAd.ClientId!;
            audiences.Add(clientId);
            audiences.Add($"api://{clientId}");
        }

        if (options.Google?.IsValid == true)
        {
            audiences.Add(options.Google.ClientId!);
        }

        if (options.Generic?.IsValid == true)
        {
            audiences.Add(options.Generic.ClientId!);
        }

        if (options.Okta?.IsValid == true)
        {
            audiences.Add(options.Okta.ClientId!);
        }

        if (options.Auth0?.IsValid == true)
        {
            audiences.Add(options.Auth0.ClientId!);
            if (!string.IsNullOrWhiteSpace(options.Auth0.Audience))
            {
                audiences.Add(options.Auth0.Audience);
            }
        }

        foreach (var audience in options.TokenValidation.ValidAudiences)
        {
            audiences.Add(audience);
        }

        return [.. audiences];
    }

    private static string? ResolveAuthority(OidcAuthenticationOptions options)
    {
        if (options.AzureAd?.IsValid == true)
        {
            return $"{options.AzureAd.Instance}{options.AzureAd.TenantId}/v2.0";
        }

        if (options.Generic?.IsValid == true)
        {
            return options.Generic.Authority;
        }

        if (options.Okta?.IsValid == true)
        {
            return options.Okta.GetAuthority();
        }

        if (options.Auth0?.IsValid == true)
        {
            return options.Auth0.GetAuthority();
        }

        if (options.Google?.IsValid == true)
        {
            return "https://accounts.google.com";
        }

        return null;
    }
}
