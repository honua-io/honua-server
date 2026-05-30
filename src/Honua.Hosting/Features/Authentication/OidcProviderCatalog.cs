// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal sealed record OidcProviderRegistration(
    string Key,
    string Type,
    string DisplayName,
    string? Authority,
    string? ClientId,
    string? ClientSecret,
    string[] Scopes,
    string? CallbackPath,
    bool Enabled,
    bool IsValid);

internal static class OidcProviderCatalog
{
    public static IReadOnlyList<OidcProviderRegistration> GetProviders(OidcAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var providers = new List<OidcProviderRegistration>();

        if (options.AzureAd is { } azureAd)
        {
            providers.Add(new OidcProviderRegistration(
                Key: "azuread",
                Type: "AzureAd",
                DisplayName: "Microsoft Entra ID",
                Authority: BuildAzureAdAuthority(azureAd),
                ClientId: azureAd.ClientId,
                ClientSecret: azureAd.ClientSecret,
                Scopes: azureAd.Scopes,
                CallbackPath: azureAd.CallbackPath,
                Enabled: azureAd.Enabled,
                IsValid: azureAd.IsValid));
        }

        if (options.Google is { } google)
        {
            providers.Add(new OidcProviderRegistration(
                Key: "google",
                Type: "Google",
                DisplayName: "Google",
                Authority: "https://accounts.google.com",
                ClientId: google.ClientId,
                ClientSecret: google.ClientSecret,
                Scopes: google.Scopes,
                CallbackPath: google.CallbackPath,
                Enabled: google.Enabled,
                IsValid: google.IsValid));
        }

        if (options.Okta is { } okta)
        {
            providers.Add(new OidcProviderRegistration(
                Key: "okta",
                Type: "Okta",
                DisplayName: "Okta",
                Authority: okta.IsValid ? okta.GetAuthority() : null,
                ClientId: okta.ClientId,
                ClientSecret: okta.ClientSecret,
                Scopes: okta.Scopes,
                CallbackPath: okta.CallbackPath,
                Enabled: okta.Enabled,
                IsValid: okta.IsValid));
        }

        if (options.Auth0 is { } auth0)
        {
            providers.Add(new OidcProviderRegistration(
                Key: "auth0",
                Type: "Auth0",
                DisplayName: "Auth0",
                Authority: auth0.IsValid ? auth0.GetAuthority() : null,
                ClientId: auth0.ClientId,
                ClientSecret: auth0.ClientSecret,
                Scopes: auth0.Scopes,
                CallbackPath: auth0.CallbackPath,
                Enabled: auth0.Enabled,
                IsValid: auth0.IsValid));
        }

        if (options.Generic is { } generic)
        {
            providers.Add(new OidcProviderRegistration(
                Key: "oidc",
                Type: "Generic",
                DisplayName: generic.DisplayName,
                Authority: generic.Authority,
                ClientId: generic.ClientId,
                ClientSecret: generic.ClientSecret,
                Scopes: generic.Scopes,
                CallbackPath: generic.CallbackPath,
                Enabled: generic.Enabled,
                IsValid: generic.IsValid));
        }

        return providers;
    }

    public static bool TryResolveProvider(
        OidcAuthenticationOptions options,
        string providerKey,
        out OidcProviderRegistration provider)
    {
        provider = default!;

        if (!options.Enabled || string.IsNullOrWhiteSpace(providerKey))
        {
            return false;
        }

        provider = GetProviders(options)
            .FirstOrDefault(candidate =>
                candidate.IsValid &&
                (candidate.Key.Equals(providerKey.Trim(), StringComparison.OrdinalIgnoreCase)
                 || candidate.Type.Equals(providerKey.Trim(), StringComparison.OrdinalIgnoreCase)))!;

        return provider is not null;
    }

    private static string? BuildAzureAdAuthority(AzureAdProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Instance) || string.IsNullOrWhiteSpace(options.TenantId))
        {
            return null;
        }

        return $"{options.Instance.TrimEnd('/')}/{options.TenantId}/v2.0";
    }
}
