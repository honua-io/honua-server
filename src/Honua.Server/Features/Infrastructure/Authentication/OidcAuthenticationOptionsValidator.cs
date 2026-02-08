// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
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
                                    (options.Generic?.Enabled == true);

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
    }

    /// <summary>
    /// Validates token validation configuration.
    /// </summary>
    private static void ValidateTokenValidation(TokenValidationOptions tokenValidation, List<string> failures)
    {
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
