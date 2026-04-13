// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Validates OidcAuthenticationOptions configuration to ensure secure OIDC authentication setup.
/// Enforces provider-specific validation rules and security best practices.
/// </summary>
internal sealed class OidcAuthenticationOptionsValidator : ConfigurationValidator<OidcAuthenticationOptions>
{
    /// <summary>
    /// Validates the OIDC authentication options configuration using derived class-specific logic.
    /// </summary>
    /// <param name="options">The OIDC authentication options instance to validate</param>
    /// <param name="errors">List to add validation errors to</param>
    protected override void PerformFeatureSpecificValidation(OidcAuthenticationOptions options, List<string> errors)
    {
        // Complex business rule validations
        ValidateGeneralSettings(options, errors);
        ValidateProviderSettings(options, errors);
        ValidateClaimsMapping(options.ClaimsMapping, errors);
        ValidateTokenValidation(options.TokenValidation, errors);
    }


    /// <summary>
    /// Validates general OIDC settings.
    /// </summary>
    private static void ValidateGeneralSettings(OidcAuthenticationOptions options, List<string> errors)
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
                errors.Add("At least one OIDC provider must be enabled when OIDC authentication is enabled");
            }
        }

        // Validate DefaultRole
        ValidateRequiredString(options.DefaultRole, "DefaultRole", errors);
        ValidateStringLength(options.DefaultRole, 50, "DefaultRole", errors);

        // Validate AdminRoles
        ValidateCollectionCount(options.AdminRoles, 1, int.MaxValue, "AdminRoles", errors);

        if (options.AdminRoles != null)
        {
            foreach (var role in options.AdminRoles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    errors.Add("AdminRoles cannot contain empty or whitespace-only role names");
                }
                else
                {
                    ValidateStringLength(role, 50, $"AdminRoles role '{role}'", errors);
                }
            }
        }

        // HTTPS requirement validation
        if (options.Enabled && !options.RequireHttps)
        {
            errors.Add("RequireHttps should be true for OIDC authentication in production environments for security");
        }
    }

    /// <summary>
    /// Validates provider-specific settings.
    /// </summary>
    private static void ValidateProviderSettings(OidcAuthenticationOptions options, List<string> errors)
    {
        // Validate Azure AD provider
        if (options.AzureAd?.Enabled == true)
        {
            ValidateAzureAdProvider(options.AzureAd, errors);
        }

        // Validate Google provider
        if (options.Google?.Enabled == true)
        {
            ValidateGoogleProvider(options.Google, errors);
        }

        // Validate Generic provider
        if (options.Generic?.Enabled == true)
        {
            ValidateGenericProvider(options.Generic, errors);
        }

        // Validate Okta provider
        if (options.Okta?.Enabled == true)
        {
            ValidateOktaProvider(options.Okta, errors);
        }

        // Validate Auth0 provider
        if (options.Auth0?.Enabled == true)
        {
            ValidateAuth0Provider(options.Auth0, errors);
        }

        // Check for conflicting callback paths
        ValidateCallbackPaths(options, errors);
    }

    /// <summary>
    /// Validates Azure AD provider configuration.
    /// </summary>
    private static void ValidateAzureAdProvider(AzureAdProviderOptions azureAd, List<string> errors)
    {
        ValidateRequiredString(azureAd.TenantId, "AzureAd.TenantId", errors);
        if (!string.IsNullOrWhiteSpace(azureAd.TenantId) &&
            !IsValidGuid(azureAd.TenantId) &&
            azureAd.TenantId != "common" && azureAd.TenantId != "organizations" && azureAd.TenantId != "consumers")
        {
            errors.Add("AzureAd.TenantId must be a valid GUID or one of 'common', 'organizations', 'consumers'");
        }

        ValidateRequiredString(azureAd.ClientId, "AzureAd.ClientId", errors);
        if (!string.IsNullOrWhiteSpace(azureAd.ClientId))
        {
            ValidateGuid(azureAd.ClientId, "AzureAd.ClientId", errors);
        }

        // Instance URL validation
        ValidateUrl(azureAd.Instance, "AzureAd.Instance", errors, requireHttps: true);

        ValidateCallbackPath(azureAd.CallbackPath, "AzureAd.CallbackPath", errors);
        ValidateCallbackPath(azureAd.SignedOutCallbackPath, "AzureAd.SignedOutCallbackPath", errors);
        ValidateScopes(azureAd.Scopes, "AzureAd.Scopes", errors);
    }

    /// <summary>
    /// Validates Google provider configuration.
    /// </summary>
    private static void ValidateGoogleProvider(GoogleProviderOptions google, List<string> errors)
    {
        ValidateRequiredString(google.ClientId, "Google.ClientId", errors);
        ValidateRequiredString(google.ClientSecret, "Google.ClientSecret", errors);

        ValidateCallbackPath(google.CallbackPath, "Google.CallbackPath", errors);
        ValidateScopes(google.Scopes, "Google.Scopes", errors);
    }

    /// <summary>
    /// Validates Generic OIDC provider configuration.
    /// </summary>
    private static void ValidateGenericProvider(GenericOidcProviderOptions generic, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(generic.Authority))
        {
            errors.Add("Generic.Authority cannot be empty when Generic OIDC is enabled");
        }
        else if (!Uri.TryCreate(generic.Authority, UriKind.Absolute, out var authorityUri))
        {
            errors.Add("Generic.Authority must be a valid absolute URL");
        }
        else if (authorityUri.Scheme != "https")
        {
            errors.Add("Generic.Authority must use HTTPS for security");
        }

        if (string.IsNullOrWhiteSpace(generic.ClientId))
        {
            errors.Add("Generic.ClientId cannot be empty when Generic OIDC is enabled");
        }

        // Display name validation
        if (string.IsNullOrWhiteSpace(generic.DisplayName))
        {
            errors.Add("Generic.DisplayName cannot be empty");
        }
        else if (generic.DisplayName.Length > 100)
        {
            errors.Add("Generic.DisplayName should not exceed 100 characters");
        }

        // Response type validation
        var validResponseTypes = new[] { "code", "id_token", "token", "id_token token", "code id_token", "code token", "code id_token token" };
        if (!validResponseTypes.Contains(generic.ResponseType))
        {
            errors.Add($"Generic.ResponseType '{generic.ResponseType}' is not a valid OIDC response type");
        }

        ValidateCallbackPath(generic.CallbackPath, "Generic.CallbackPath", errors);
        ValidateCallbackPath(generic.SignedOutCallbackPath, "Generic.SignedOutCallbackPath", errors);
        ValidateScopes(generic.Scopes, "Generic.Scopes", errors);
    }

    /// <summary>
    /// Validates Okta provider configuration.
    /// </summary>
    private static void ValidateOktaProvider(OktaProviderOptions okta, List<string> errors)
    {
        ValidateRequiredString(okta.OrgUrl, "Okta.OrgUrl", errors);
        if (!string.IsNullOrWhiteSpace(okta.OrgUrl))
        {
            if (okta.OrgUrl.Contains("://"))
            {
                errors.Add("Okta.OrgUrl must be a domain only (e.g. 'dev-12345.okta.com'), not a full URL");
            }
            else if (!Uri.TryCreate($"https://{okta.OrgUrl}", UriKind.Absolute, out _))
            {
                errors.Add("Okta.OrgUrl must form a valid hostname");
            }
        }

        ValidateRequiredString(okta.ClientId, "Okta.ClientId", errors);

        if (!string.IsNullOrEmpty(okta.AuthorizationServerId) && string.IsNullOrWhiteSpace(okta.AuthorizationServerId))
        {
            errors.Add("Okta.AuthorizationServerId cannot be whitespace-only");
        }

        ValidateCallbackPath(okta.CallbackPath, "Okta.CallbackPath", errors);
        ValidateCallbackPath(okta.SignedOutCallbackPath, "Okta.SignedOutCallbackPath", errors);
        ValidateScopes(okta.Scopes, "Okta.Scopes", errors);
    }

    /// <summary>
    /// Validates Auth0 provider configuration.
    /// </summary>
    private static void ValidateAuth0Provider(Auth0ProviderOptions auth0, List<string> errors)
    {
        ValidateRequiredString(auth0.Domain, "Auth0.Domain", errors);
        if (!string.IsNullOrWhiteSpace(auth0.Domain))
        {
            if (auth0.Domain.Contains("://"))
            {
                errors.Add("Auth0.Domain must be a domain only (e.g. 'myapp.us.auth0.com'), not a full URL");
            }
            else if (!Uri.TryCreate($"https://{auth0.Domain}", UriKind.Absolute, out _))
            {
                errors.Add("Auth0.Domain must form a valid hostname");
            }
        }

        ValidateRequiredString(auth0.ClientId, "Auth0.ClientId", errors);

        if (!string.IsNullOrEmpty(auth0.Audience) && string.IsNullOrWhiteSpace(auth0.Audience))
        {
            errors.Add("Auth0.Audience cannot be whitespace-only when set");
        }

        if (!string.IsNullOrWhiteSpace(auth0.RoleClaimNamespace))
        {
            if (!Uri.TryCreate(auth0.RoleClaimNamespace, UriKind.Absolute, out var nsUri))
            {
                errors.Add("Auth0.RoleClaimNamespace must be a valid absolute URI when set");
            }
            else if (nsUri.Scheme != "https")
            {
                errors.Add("Auth0.RoleClaimNamespace must use HTTPS");
            }
        }

        ValidateCallbackPath(auth0.CallbackPath, "Auth0.CallbackPath", errors);
        ValidateCallbackPath(auth0.SignedOutCallbackPath, "Auth0.SignedOutCallbackPath", errors);
        ValidateScopes(auth0.Scopes, "Auth0.Scopes", errors);
    }

    /// <summary>
    /// Validates that callback paths are unique across providers.
    /// </summary>
    private static void ValidateCallbackPaths(OidcAuthenticationOptions options, List<string> errors)
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
            errors.Add($"Callback path '{duplicate.Key}' is used by multiple providers: {providers}");
        }
    }

    /// <summary>
    /// Validates claims mapping configuration.
    /// </summary>
    private static void ValidateClaimsMapping(ClaimsMappingOptions claimsMapping, List<string> errors)
    {
        ValidateRequiredString(claimsMapping.NameClaimType, "ClaimsMapping.NameClaimType", errors);
        ValidateRequiredString(claimsMapping.RoleClaimType, "ClaimsMapping.RoleClaimType", errors);
        ValidateRequiredString(claimsMapping.EmailClaimType, "ClaimsMapping.EmailClaimType", errors);
        ValidateRequiredString(claimsMapping.UserIdClaimType, "ClaimsMapping.UserIdClaimType", errors);

        // Validate custom mappings
        if (claimsMapping.CustomMappings != null)
        {
            foreach (var mapping in claimsMapping.CustomMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.Key))
                {
                    errors.Add("ClaimsMapping.CustomMappings cannot have empty source claim names");
                }

                if (string.IsNullOrWhiteSpace(mapping.Value))
                {
                    errors.Add($"ClaimsMapping.CustomMappings['{mapping.Key}'] cannot have empty target claim name");
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
                    errors.Add("ClaimsMapping.AdditionalRoleClaimTypes cannot contain empty or whitespace-only entries");
                }
            }
        }
    }

    /// <summary>
    /// Validates token validation configuration.
    /// </summary>
    private static void ValidateTokenValidation(TokenValidationOptions tokenValidation, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(tokenValidation.SymmetricSigningKey))
        {
            var keyBytes = Encoding.UTF8.GetByteCount(tokenValidation.SymmetricSigningKey);
            if (keyBytes < 32)
            {
                errors.Add("TokenValidation.SymmetricSigningKey must be at least 32 UTF-8 bytes for HS256 security.");
            }
        }

        // Clock skew validation
        ValidateTimeSpan(tokenValidation.ClockSkew, TimeSpan.Zero, TimeSpan.FromMinutes(30), "TokenValidation.ClockSkew", errors);

        // Token replay protection validation
        if (tokenValidation.EnableTokenReplayProtection)
        {
            if (tokenValidation.TokenReplayCacheDuration < TimeSpan.Zero)
            {
                errors.Add("TokenValidation.TokenReplayCacheDuration must be positive");
            }
            else if (tokenValidation.TokenReplayCacheDuration > TimeSpan.FromHours(24))
            {
                errors.Add("TokenValidation.TokenReplayCacheDuration must be between 0 seconds and 24 hours");
            }
        }
    }

    /// <summary>
    /// Validates a callback path format.
    /// </summary>
    private static void ValidateCallbackPath(string path, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{propertyName} cannot be empty");
            return;
        }

        if (!path.StartsWith('/'))
        {
            errors.Add($"{propertyName} must start with '/' to be a valid path");
        }

        if (path.Contains("//"))
        {
            errors.Add($"{propertyName} cannot contain consecutive slashes");
        }

        if (path.Length > 200)
        {
            errors.Add($"{propertyName} should not exceed 200 characters");
        }
    }

    /// <summary>
    /// Validates OIDC scopes.
    /// </summary>
    private static void ValidateScopes(string[] scopes, string propertyName, List<string> errors)
    {
        if (scopes == null || scopes.Length == 0)
        {
            errors.Add($"{propertyName} must contain at least one scope");
            return;
        }

        // OpenID Connect requires 'openid' scope
        if (!scopes.Contains("openid", StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"{propertyName} must include 'openid' scope for OIDC compliance");
        }

        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                errors.Add($"{propertyName} cannot contain empty or whitespace-only scope names");
            }
            else if (scope.Length > 100)
            {
                errors.Add($"{propertyName} scope '{scope}' should not exceed 100 characters");
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
