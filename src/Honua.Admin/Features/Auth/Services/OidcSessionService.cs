// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Honua.Admin.Features.Auth.Models;
using Microsoft.AspNetCore.Components;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Manages the OIDC authorization-code-with-PKCE flow, token exchange, refresh, and logout.
/// </summary>
public sealed class OidcSessionService
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly AuthStateStore _store;
    private readonly AuthBootstrapService _bootstrap;
    private readonly NavigationManager _nav;

    // Cache discovery documents per authority to avoid repeated fetches.
    private readonly Dictionary<string, OidcDiscoveryDocument> _discoveryCache = new(StringComparer.OrdinalIgnoreCase);

    public OidcSessionService(
        HttpClient http,
        AuthStateStore store,
        AuthBootstrapService bootstrap,
        NavigationManager nav)
    {
        _http = http;
        _store = store;
        _bootstrap = bootstrap;
        _nav = nav;
    }

    /// <summary>
    /// Initiates the authorization code + PKCE flow by redirecting to the provider's authorization endpoint.
    /// </summary>
    public async Task StartLoginAsync(AuthProviderInfo provider)
    {
        var discovery = await GetDiscoveryDocumentAsync(provider.Authority);

        var (verifier, challenge) = GeneratePkceParams();
        var state = GenerateRandomString(32);

        await _store.ClearTokensAsync(); // Clear stale tokens but preserve nothing else
        await _store.StorePkceStateAsync(verifier, state);
        await _store.StoreSelectedProviderKeyAsync(provider.Key);

        var redirectUri = BuildAbsoluteUri(provider.RedirectPath);
        var scope = string.Join(" ", provider.Scopes);

        var authUrl = $"{discovery.AuthorizationEndpoint}" +
            $"?response_type=code" +
            $"&client_id={Uri.EscapeDataString(provider.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256";

        _nav.NavigateTo(authUrl, forceLoad: true);
    }

    /// <summary>
    /// Handles the callback from the OIDC provider: validates state, exchanges the code for tokens.
    /// Returns true on success.
    /// </summary>
    public async Task<(bool Success, string? Error)> HandleCallbackAsync(string code, string state, AuthProviderInfo provider)
    {
        var (storedVerifier, storedState) = await _store.GetPkceStateAsync();

        if (storedState is null || !string.Equals(storedState, state, StringComparison.Ordinal))
        {
            return (false, "Invalid state parameter. Possible CSRF attack.");
        }

        if (storedVerifier is null)
        {
            return (false, "PKCE verifier not found. Please try logging in again.");
        }

        await _store.ClearPkceStateAsync();

        var discovery = await GetDiscoveryDocumentAsync(provider.Authority);
        var redirectUri = BuildAbsoluteUri(provider.RedirectPath);

        var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = provider.ClientId,
            ["code_verifier"] = storedVerifier,
        });

        try
        {
            var response = await _http.PostAsync(discovery.TokenEndpoint, tokenRequest);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return (false, $"Token exchange failed: {response.StatusCode} - {errorBody}");
            }

            var tokens = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.TokenResponse);

            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            {
                return (false, "Empty token response from provider.");
            }

            await _store.StoreTokensAsync(
                tokens.AccessToken,
                tokens.IdToken,
                tokens.RefreshToken,
                tokens.ExpiresIn,
                provider.Key);

            return (true, null);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Token exchange request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts a silent token refresh using the stored refresh token.
    /// Returns true if tokens were successfully refreshed.
    /// </summary>
    public async Task<bool> TryRefreshAsync()
    {
        var refreshToken = await _store.GetRefreshTokenAsync();
        var providerKey = await _store.GetProviderKeyAsync();

        if (refreshToken is null || providerKey is null)
            return false;

        var config = await _bootstrap.GetConfigAsync();
        var provider = config.Providers.Find(p => p.Key == providerKey);
        if (provider is null)
            return false;

        var discovery = await GetDiscoveryDocumentAsync(provider.Authority);

        var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = provider.ClientId,
        });

        try
        {
            var response = await _http.PostAsync(discovery.TokenEndpoint, tokenRequest);
            if (!response.IsSuccessStatusCode)
                return false;

            var tokens = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.TokenResponse);
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
                return false;

            await _store.StoreTokensAsync(
                tokens.AccessToken,
                tokens.IdToken,
                tokens.RefreshToken ?? refreshToken, // Keep old refresh token if new one not issued
                tokens.ExpiresIn,
                provider.Key);

            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a proactive refresh is needed and attempts it.
    /// Returns true if the session is still valid (either fresh or successfully refreshed).
    /// </summary>
    public async Task<bool> EnsureValidSessionAsync()
    {
        if (!await _store.IsTokenExpiringSoonAsync(RefreshBuffer))
            return true;

        return await TryRefreshAsync();
    }

    /// <summary>
    /// Clears local session and redirects to IdP logout endpoint when available.
    /// </summary>
    public async Task LogoutAsync()
    {
        var providerKey = await _store.GetProviderKeyAsync();
        var idToken = await _store.GetIdTokenAsync();

        await _store.ClearAsync();

        if (providerKey is null)
        {
            _nav.NavigateTo("/admin/auth/login", forceLoad: true);
            return;
        }

        var config = await _bootstrap.GetConfigAsync();
        var provider = config.Providers.Find(p => p.Key == providerKey);

        if (provider?.SupportsLogout == true)
        {
            var discovery = await GetDiscoveryDocumentAsync(provider.Authority);
            if (!string.IsNullOrEmpty(discovery.EndSessionEndpoint))
            {
                var logoutUrl = discovery.EndSessionEndpoint +
                    $"?post_logout_redirect_uri={Uri.EscapeDataString(BuildAbsoluteUri(provider.PostLogoutRedirectPath ?? "/admin"))}" +
                    (idToken is not null ? $"&id_token_hint={Uri.EscapeDataString(idToken)}" : "");

                _nav.NavigateTo(logoutUrl, forceLoad: true);
                return;
            }
        }

        // Fallback: local-only logout
        _nav.NavigateTo("/admin/auth/login", forceLoad: true);
    }

    private async Task<OidcDiscoveryDocument> GetDiscoveryDocumentAsync(string authority)
    {
        if (_discoveryCache.TryGetValue(authority, out var cached))
            return cached;

        var discoveryUrl = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var doc = await _http.GetFromJsonAsync(discoveryUrl, AuthJsonContext.Default.OidcDiscoveryDocument)
            ?? throw new InvalidOperationException($"Failed to fetch OIDC discovery document from {discoveryUrl}");

        _discoveryCache[authority] = doc;
        return doc;
    }

    private string BuildAbsoluteUri(string path)
    {
        return new Uri(_nav.BaseUri).GetLeftPart(UriPartial.Authority) + path;
    }

    private static (string Verifier, string Challenge) GeneratePkceParams()
    {
        var verifier = GenerateRandomString(64);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(challengeBytes);
        return (verifier, challenge);
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        for (int i = 0; i < length; i++)
            result[i] = chars[bytes[i] % chars.Length];
        return new string(result);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
