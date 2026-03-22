// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Admin.Features.Auth.Models;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Fetches the auth configuration from the server bootstrap endpoint
/// and caches it for the lifetime of the WASM app.
/// </summary>
public sealed class AuthBootstrapService
{
    private readonly HttpClient _http;
    private AdminAuthConfig? _config;

    public AuthBootstrapService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Gets the cached auth configuration, fetching from the server on first call.
    /// </summary>
    public async Task<AdminAuthConfig> GetConfigAsync()
    {
        if (_config is not null)
            return _config;

        _config = await _http.GetFromJsonAsync(
            "api/v1/admin/auth/config",
            AuthJsonContext.Default.AdminAuthConfig);

        return _config ?? new AdminAuthConfig();
    }

    /// <summary>
    /// Determines the auth mode from the configuration.
    /// </summary>
    public async Task<AuthMode> GetAuthModeAsync()
    {
        var config = await GetConfigAsync();

        if (!config.OidcEnabled || config.Providers.Count == 0)
            return AuthMode.ApiKey;

        return config.Providers.Count == 1
            ? AuthMode.OidcSingleProvider
            : AuthMode.OidcMultiProvider;
    }

    /// <summary>
    /// Gets the single OIDC provider when exactly one is configured.
    /// Returns null if zero or multiple providers exist.
    /// </summary>
    public async Task<AuthProviderInfo?> GetSingleProviderAsync()
    {
        var config = await GetConfigAsync();
        return config.Providers.Count == 1 ? config.Providers[0] : null;
    }
}

/// <summary>
/// Authentication modes determined by server configuration.
/// </summary>
public enum AuthMode
{
    /// <summary>No OIDC providers configured; use API key authentication.</summary>
    ApiKey,

    /// <summary>Exactly one OIDC provider configured; auto-redirect to login.</summary>
    OidcSingleProvider,

    /// <summary>Multiple OIDC providers configured; show provider chooser.</summary>
    OidcMultiProvider
}
