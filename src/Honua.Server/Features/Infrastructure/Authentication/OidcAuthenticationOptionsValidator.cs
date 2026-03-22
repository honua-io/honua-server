// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Validates OidcAuthenticationOptions configuration to ensure secure OIDC authentication setup.
/// Enforces provider-specific validation rules and security best practices.
/// </summary>
internal sealed class OidcAuthenticationOptionsValidator : OptionsValidator<OidcAuthenticationOptions>
{
    /// <summary>
    /// Validates the OIDC authentication options configuration using derived class-specific logic.
    /// </summary>
    /// <param name="options">The OIDC authentication options instance to validate</param>
    /// <param name="failures">List to add validation errors to</param>
    protected override void ValidateOptions(OidcAuthenticationOptions options, List<string> failures)
    {
        // Complex business rule validations
        ValidateGeneralSettings(options, failures);
        ValidateProviderSettings(options, failures);
        ValidateClaimsMapping(options.ClaimsMapping, failures);
        ValidateTokenValidation(options.TokenValidation, failures);
    }


    /// <summary>
    /// Validates general OIDC settings.
    /// </summary>
    private static void ValidateGeneralSettings(OidcAuthenticationOptions options, List<string> failures)
    {
        // When OIDC is enabled, at least one provider must be configured
        if (options.Enabled)
        {
            var hasEnabledProvider = (options.AzureAd?.Enabled == true) ||
                                    (options.Google?.Enabled == true) ||
                                    (options.Generic?.Enabled == true) ||
                                    (options.Okta?.Enabled == true) ||
                                    (options.Auth0?.Enabled == true);

            if (!hasEnabledProvider)
            {
                failures.Add("At least one OIDC provider must be enabled when OIDC authentication is enabled");
            }
        }

        // Validate DefaultRole
        ValidateRequiredString(options.DefaultRole, "DefaultRole", failures);
        ValidateStringLength(options.DefaultRole, 50, "DefaultRole", failures);

        // Validate AdminRoles
        ValidateCollectionCount(options.AdminRoles, 1, int.MaxValue, "AdminRoles", failures);

        if (options.AdminRoles != null)
        {
            foreach (var role in options.AdminRoles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    failures.Add("AdminRoles cannot contain empty or whitespace-only role names");
                }
                else
                {
                    ValidateStringLength(role, 50, $"AdminRoles role '{role}'", failures);
                }
            }
        }

        // HTTPS requirement validation
        if (options.Enabled && !options.RequireHttps)
        {
            failures.Add("RequireHttps should be true for OIDC authentication in production environments for security");
        }
    }

    /// <summary>
    /// Validates provider-specific settings.
    /// </summary>
    private static void ValidateProviderSettings(OidcAuthenticationOptions options, List<string> failures)
    {
        // Validate Azure AD provider
        if (options.AzureAd?.Enabled == true)
        {
            ValidateAzureAdProvider(options.AzureAd, failures);
        }

        // Validate Google provider
        if (options.Google?.Enabled == true)
        {
            ValidateGoogleProvider(options.Google, failures);
        }

        // Validate Generic provider
        if (options.Generic?.Enabled == true)
        {
            ValidateGenericProvider(options.Generic, failures);
        }

        // Validate Okta provider
        if (options.Okta?.Enabled == true)
        {
            ValidateOktaProvider(options.Okta, failures);
        }

        // Validate Auth0 provider
        if (options.Auth0?.Enabled == true)
        {
            ValidateAuth0Provider(options.Auth0, failures);
        }

        // Check for conflicting callback paths
        ValidateCallbackPaths(options, failures);
    }

    /// <summary>
    /// Validates Azure AD provider configuration.
    /// </summary>
    private static void ValidateAzureAdProvider(AzureAdProviderOptions azureAd, List<string> failures)
    {
        ValidateRequiredString(azureAd.TenantId, "AzureAd.TenantId", failures);
        if (!string.IsNullOrWhiteSpace(azureAd.TenantId) &&
            !IsValidGuid(azureAd.TenantId) &&
            azureAd.TenantId != "common" && azureAd.TenantId != "organizations" && azureAd.TenantId != "consumers")
        {
            failures.Add("AzureAd.TenantId must be a valid GUID or one of 'common', 'organizations', 'consumers'");
        }

        ValidateRequiredString(azureAd.ClientId, "AzureAd.ClientId", failures);
        if (!string.IsNullOrWhiteSpace(azureAd.ClientId))
        {
            ValidateGuid(azureAd.ClientId, "AzureAd.ClientId", failures);
        }

        // Instance URL validation
        ValidateUrl(azureAd.Instance, "AzureAd.Instance", failures, requireHttps: true);

        ValidateCallbackPath(azureAd.CallbackPath, "AzureAd.CallbackPath", failures);
        ValidateCallbackPath(azureAd.SignedOutCallbackPath, "AzureAd.SignedOutCallbackPath", failures);
        ValidateScopes(azureAd.Scopes, "AzureAd.Scopes", failures);
    }

    /// <summary>
    /// Validates Google provider configuration.
    /// </summary>
    private static void ValidateGoogleProvider(GoogleProviderOptions google, List<string> failures)
    {
        ValidateRequiredString(google.ClientId, "Google.ClientId", failures);
        ValidateRequiredString(google.ClientSecret, "Google.ClientSecret", failures);

        ValidateCallbackPath(google.CallbackPath, "Google.CallbackPath", failures);
        ValidateScopes(google.Scopes, "Google.Scopes", failures);
    }

    /// <summary>
    /// Validates Generic OIDC provider configuration.
    /// </summary>
    private static void ValidateGenericProvider(GenericOidcProviderOptions generic, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(generic.Authority))
        {
            failures.Add("Generic.Authority cannot be empty when Generic OIDC is enabled");
        }
        else if (!Uri.TryCreate(generic.Authority, UriKind.Absolute, out var authorityUri))
        {
            failures.Add("Generic.Authority must be a valid absolute URL");
        }
        else if (authorityUri.Scheme != "https")
        {
            failures.Add("Generic.Authority must use HTTPS for security");
        }

        if (string.IsNullOrWhiteSpace(generic.ClientId))
        {
            failures.Add("Generic.ClientId cannot be empty when Generic OIDC is enabled");
        }

        // Display name validation
        if (string.IsNullOrWhiteSpace(generic.DisplayName))
        {
            failures.Add("Generic.DisplayName cannot be empty");
        }
        else if (generic.DisplayName.Length > 100)
        {
            failures.Add("Generic.DisplayName should not exceed 100 characters");
        }

        // Response type validation
        var validResponseTypes = new[] { "code", "id_token", "token", "id_token token", "code id_token", "code token", "code id_token token" };
        if (!validResponseTypes.Contains(generic.ResponseType))
        {
            failures.Add($"Generic.ResponseType '{generic.ResponseType}' is not a valid OIDC response type");
        }

        ValidateCallbackPath(generic.CallbackPath, "Generic.CallbackPath", failures);
        ValidateCallbackPath(generic.SignedOutCallbackPath, "Generic.SignedOutCallbackPath", failures);
        ValidateScopes(generic.Scopes, "Generic.Scopes", failures);
    }

    /// <summary>
    /// Validates Okta provider configuration.
    /// </summary>
    private static void ValidateOktaProvider(OktaProviderOptions okta, List<string> failures)
    {
        ValidateRequiredString(okta.OrgUrl, "Okta.OrgUrl", failures);
        if (!string.IsNullOrWhiteSpace(okta.OrgUrl))
        {
            if (okta.OrgUrl.Contains("://"))
            {
                failures.Add("Okta.OrgUrl must be a domain only (e.g. 'dev-12345.okta.com'), not a full URL");
            }
            else if (!Uri.TryCreate($"https://{okta.OrgUrl}", UriKind.Absolute, out _))
            {
                failures.Add("Okta.OrgUrl must form a valid hostname");
            }
        }

        ValidateRequiredString(okta.ClientId, "Okta.ClientId", failures);

        if (!string.IsNullOrEmpty(okta.AuthorizationServerId) && string.IsNullOrWhiteSpace(okta.AuthorizationServerId))
        {
            failures.Add("Okta.AuthorizationServerId cannot be whitespace-only");
        }

        ValidateCallbackPath(okta.CallbackPath, "Okta.CallbackPath", failures);
        ValidateCallbackPath(okta.SignedOutCallbackPath, "Okta.SignedOutCallbackPath", failures);
        ValidateScopes(okta.Scopes, "Okta.Scopes", failures);
    }

    /// <summary>
    /// Validates Auth0 provider configuration.
    /// </summary>
    private static void ValidateAuth0Provider(Auth0ProviderOptions auth0, List<string> failures)
    {
        ValidateRequiredString(auth0.Domain, "Auth0.Domain", failures);
        if (!string.IsNullOrWhiteSpace(auth0.Domain))
        {
            if (auth0.Domain.Contains("://"))
            {
                failures.Add("Auth0.Domain must be a domain only (e.g. 'myapp.us.auth0.com'), not a full URL");
            }
            else if (!Uri.TryCreate($"https://{auth0.Domain}", UriKind.Absolute, out _))
            {
                failures.Add("Auth0.Domain must form a valid hostname");
            }
        }

        ValidateRequiredString(auth0.ClientId, "Auth0.ClientId", failures);

        if (!string.IsNullOrEmpty(auth0.Audience) && string.IsNullOrWhiteSpace(auth0.Audience))
        {
            failures.Add("Auth0.Audience cannot be whitespace-only when set");
        }

        if (!string.IsNullOrWhiteSpace(auth0.RoleClaimNamespace))
        {
            if (!Uri.TryCreate(auth0.RoleClaimNamespace, UriKind.Absolute, out var nsUri))
            {
                failures.Add("Auth0.RoleClaimNamespace must be a valid absolute URI when set");
            }
            else if (nsUri.Scheme != "https")
            {
                failures.Add("Auth0.RoleClaimNamespace must use HTTPS");
            }
        }

        ValidateCallbackPath(auth0.CallbackPath, "Auth0.CallbackPath", failures);
        ValidateCallbackPath(auth0.SignedOutCallbackPath, "Auth0.SignedOutCallbackPath", failures);
        ValidateScopes(auth0.Scopes, "Auth0.Scopes", failures);
    }

    /// <summary>
    /// Validates that callback paths are unique across providers.
    /// </summary>
    private static void ValidateCallbackPaths(OidcAuthenticationOptions options, List<string> failures)
    {
        var callbackPaths = new List<(string path, string provider)>();

        if (options.AzureAd?.Enabled == true)
        {
            callbackPaths.Add((options.AzureAd.CallbackPath, "AzureAd"));
            callbackPaths.Add((options.AzureAd.SignedOutCallbackPath, "AzureAd SignedOut"));
        }

        if (options.Google?.Enabled == true)
        {
            callbackPaths.Add((options.Google.CallbackPath, "Google"));
        }

        if (options.Generic?.Enabled == true)
        {
            callbackPaths.Add((options.Generic.CallbackPath, "Generic"));
            callbackPaths.Add((options.Generic.SignedOutCallbackPath, "Generic SignedOut"));
        }

        if (options.Okta?.Enabled == true)
        {
            callbackPaths.Add((options.Okta.CallbackPath, "Okta"));
            callbackPaths.Add((options.Okta.SignedOutCallbackPath, "Okta SignedOut"));
        }

        if (options.Auth0?.Enabled == true)
        {
            callbackPaths.Add((options.Auth0.CallbackPath, "Auth0"));
            callbackPaths.Add((options.Auth0.SignedOutCallbackPath, "Auth0 SignedOut"));
        }

        // Check for duplicates
        var duplicates = callbackPaths
            .GroupBy(x => x.path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var duplicate in duplicates)
        {
            var providers = string.Join(", ", duplicate.Select(x => x.provider));
            failures.Add($"Callback path '{duplicate.Key}' is used by multiple providers: {providers}");
        }
    }

    /// <summary>
    /// Validates claims mapping configuration.
    /// </summary>
    private static void ValidateClaimsMapping(ClaimsMappingOptions claimsMapping, List<string> failures)
    {
        ValidateRequiredString(claimsMapping.NameClaimType, "ClaimsMapping.NameClaimType", failures);
        ValidateRequiredString(claimsMapping.RoleClaimType, "ClaimsMapping.RoleClaimType", failures);
        ValidateRequiredString(claimsMapping.EmailClaimType, "ClaimsMapping.EmailClaimType", failures);
        ValidateRequiredString(claimsMapping.UserIdClaimType, "ClaimsMapping.UserIdClaimType", failures);

        // Validate custom mappings
        if (claimsMapping.CustomMappings != null)
        {
            foreach (var mapping in claimsMapping.CustomMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.Key))
                {
                    failures.Add("ClaimsMapping.CustomMappings cannot have empty source claim names");
                }

                if (string.IsNullOrWhiteSpace(mapping.Value))
                {
                    failures.Add($"ClaimsMapping.CustomMappings['{mapping.Key}'] cannot have empty target claim name");
                }
            }
        }

        // Validate AdditionalRoleClaimTypes
        if (claimsMapping.AdditionalRoleClaimTypes != null)
        {
            foreach (var claimType in claimsMapping.AdditionalRoleClaimTypes)
            {
                if (string.IsNullOrWhiteSpace(claimType))
                {
                    failures.Add("ClaimsMapping.AdditionalRoleClaimTypes cannot contain empty or whitespace-only entries");
                }
            }
        }
    }

    /// <summary>
    /// Validates token validation configuration.
    /// </summary>
    private static void ValidateTokenValidation(TokenValidationOptions tokenValidation, List<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(tokenValidation.SymmetricSigningKey))
        {
            var keyBytes = Encoding.UTF8.GetByteCount(tokenValidation.SymmetricSigningKey);
            if (keyBytes < 32)
            {
                failures.Add("TokenValidation.SymmetricSigningKey must be at least 32 UTF-8 bytes for HS256 security.");
            }
        }

        // Clock skew validation
        ValidateTimeSpan(tokenValidation.ClockSkew, TimeSpan.Zero, TimeSpan.FromMinutes(30), "TokenValidation.ClockSkew", failures);

        // Token replay protection validation
        if (tokenValidation.EnableTokenReplayProtection)
        {
            if (tokenValidation.TokenReplayCacheDuration < TimeSpan.Zero)
            {
                failures.Add("TokenValidation.TokenReplayCacheDuration must be positive");
            }
            else if (tokenValidation.TokenReplayCacheDuration > TimeSpan.FromHours(24))
            {
                failures.Add("TokenValidation.TokenReplayCacheDuration must be between 0 seconds and 24 hours");
            }
        }
    }

    /// <summary>
    /// Validates a callback path format.
    /// </summary>
    private static void ValidateCallbackPath(string path, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{propertyName} cannot be empty");
            return;
        }

        if (!path.StartsWith('/'))
        {
            failures.Add($"{propertyName} must start with '/' to be a valid path");
        }

        if (path.Contains("//"))
        {
            failures.Add($"{propertyName} cannot contain consecutive slashes");
        }

        if (path.Length > 200)
        {
            failures.Add($"{propertyName} should not exceed 200 characters");
        }
    }

    /// <summary>
    /// Validates OIDC scopes.
    /// </summary>
    private static void ValidateScopes(string[] scopes, string propertyName, List<string> failures)
    {
        if (scopes == null || scopes.Length == 0)
        {
            failures.Add($"{propertyName} must contain at least one scope");
            return;
        }

        // OpenID Connect requires 'openid' scope
        if (!scopes.Contains("openid", StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"{propertyName} must include 'openid' scope for OIDC compliance");
        }

        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                failures.Add($"{propertyName} cannot contain empty or whitespace-only scope names");
            }
            else if (scope.Length > 100)
            {
                failures.Add($"{propertyName} scope '{scope}' should not exceed 100 characters");
            }
        }
    }

    /// <summary>
    /// Validates if a string is a valid GUID.
    /// </summary>
    private static bool IsValidGuid(string value)
    {
        return Guid.TryParse(value, out _);
    }
}
