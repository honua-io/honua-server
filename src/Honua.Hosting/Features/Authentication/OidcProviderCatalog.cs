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
    private static IOidcRuntimeProviderSource? _runtime;

    /// <summary>
    /// Attaches the process-lifetime provider source. Catalog readers that do not receive
    /// a source argument still see admin-registered providers.
    /// </summary>
    public static void Attach(IOidcRuntimeProviderSource? runtime) => _runtime = runtime;

    public static IReadOnlyList<OidcProviderRegistration> GetProviders(OidcAuthenticationOptions options)
        => GetProviders(options, runtime: null);

    public static IReadOnlyList<OidcProviderRegistration> GetProviders(
        OidcAuthenticationOptions options,
        IOidcRuntimeProviderSource? runtime)
    {
        ArgumentNullException.ThrowIfNull(options);
        runtime ??= _runtime;

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

        if (runtime is not null)
        {
            foreach (var provider in runtime.Current)
            {
                if (!provider.Enabled
                    || string.IsNullOrWhiteSpace(provider.Authority)
                    || string.IsNullOrWhiteSpace(provider.ClientId)
                    || string.IsNullOrWhiteSpace(provider.Key))
                {
                    continue;
                }

                providers.Add(new OidcProviderRegistration(
                    Key: provider.Key,
                    Type: string.IsNullOrWhiteSpace(provider.ProviderType) ? "Generic" : provider.ProviderType,
                    DisplayName: string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Key : provider.DisplayName,
                    Authority: provider.Authority.Trim(),
                    ClientId: provider.ClientId.Trim(),
                    ClientSecret: provider.ClientSecret,
                    Scopes: ["openid", "profile", "email"],
                    CallbackPath: null,
                    Enabled: true,
                    IsValid: true));
            }
        }

        return providers;
    }

    public static bool TryResolveProvider(
        OidcAuthenticationOptions options,
        string providerKey,
        out OidcProviderRegistration provider)
        => TryResolveProvider(options, providerKey, out provider, runtime: null);

    public static bool TryResolveProvider(
        OidcAuthenticationOptions options,
        string providerKey,
        out OidcProviderRegistration provider,
        IOidcRuntimeProviderSource? runtime)
    {
        provider = default!;

        if (!options.Enabled || string.IsNullOrWhiteSpace(providerKey))
        {
            return false;
        }

        provider = GetProviders(options, runtime)
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
