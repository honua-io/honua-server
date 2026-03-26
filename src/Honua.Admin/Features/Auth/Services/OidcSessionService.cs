// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Honua.Admin.Features.Auth.Models;
using Microsoft.AspNetCore.Components;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Manages the backend-assisted OIDC authorization-code-with-PKCE flow, refresh, and logout.
/// </summary>
public sealed class OidcSessionService
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);
    private const string AuthorizationCodeGrantType = "authorization_code";
    private const string RefreshTokenGrantType = "refresh_token";
    private const string TokenExchangeFailureMessage = "Authentication failed with the identity provider. Please try again.";
    private const string TokenExchangeUnavailableMessage = "Could not reach the identity provider. Please try again.";
    private const string AuthorizeUrlFailureMessage = "Unable to start sign-in. Please try again.";

    private readonly HttpClient _http;
    private readonly AuthStateStore _store;
    private readonly NavigationManager _nav;

    public OidcSessionService(
        HttpClient http,
        AuthStateStore store,
        NavigationManager nav)
    {
        _http = http;
        _store = store;
        _nav = nav;
    }

    /// <summary>
    /// Initiates the authorization code + PKCE flow by requesting an authorize URL from the server.
    /// </summary>
    public async Task StartLoginAsync(AuthProviderInfo provider)
    {
        var (verifier, challenge) = GeneratePkceParams();
        var state = GenerateRandomString(32);

        await _store.ClearTokensAsync();
        await _store.StorePkceStateAsync(verifier, state);
        await _store.StoreSelectedProviderKeyAsync(provider.Key);

        var response = await _http.PostAsJsonAsync(
            $"api/v1/admin/auth/providers/{Uri.EscapeDataString(provider.Key)}/authorize-url",
            new AdminAuthAuthorizeUrlRequest
            {
                State = state,
                CodeChallenge = challenge
            },
            AuthJsonContext.Default.AdminAuthAuthorizeUrlRequest);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(AuthorizeUrlFailureMessage);
        }

        var authorize = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
        if (authorize is null || string.IsNullOrWhiteSpace(authorize.AuthorizeUrl))
        {
            throw new InvalidOperationException(AuthorizeUrlFailureMessage);
        }

        _nav.NavigateTo(authorize.AuthorizeUrl, forceLoad: true);
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

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"api/v1/admin/auth/providers/{Uri.EscapeDataString(provider.Key)}/token",
                new AdminAuthTokenRequest
                {
                    GrantType = AuthorizationCodeGrantType,
                    Code = code,
                    CodeVerifier = storedVerifier
                },
                AuthJsonContext.Default.AdminAuthTokenRequest);

            if (!response.IsSuccessStatusCode)
            {
                return (false, TokenExchangeFailureMessage);
            }

            var tokens = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.TokenResponse);

            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            {
                return (false, TokenExchangeFailureMessage);
            }

            await _store.StoreTokensAsync(
                tokens.AccessToken,
                tokens.IdToken,
                tokens.RefreshToken,
                tokens.ExpiresIn,
                provider.Key);

            return (true, null);
        }
        catch (HttpRequestException)
        {
            return (false, TokenExchangeUnavailableMessage);
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
        {
            return false;
        }

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"api/v1/admin/auth/providers/{Uri.EscapeDataString(providerKey)}/token",
                new AdminAuthTokenRequest
                {
                    GrantType = RefreshTokenGrantType,
                    RefreshToken = refreshToken
                },
                AuthJsonContext.Default.AdminAuthTokenRequest);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var tokens = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.TokenResponse);
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            {
                return false;
            }

            await _store.StoreTokensAsync(
                tokens.AccessToken,
                tokens.IdToken,
                tokens.RefreshToken ?? refreshToken,
                tokens.ExpiresIn,
                providerKey);

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
    /// Clears local session and redirects to a provider logout URL when available.
    /// </summary>
    public async Task LogoutAsync()
    {
        var providerKey = await _store.GetProviderKeyAsync();
        var idToken = await _store.GetIdTokenAsync();

        await _store.ClearAsync();

        if (providerKey is null)
        {
            NavigateToLocalLogin();
            return;
        }

        try
        {
            var requestUri = $"api/v1/admin/auth/providers/{Uri.EscapeDataString(providerKey)}/logout-url";
            if (!string.IsNullOrWhiteSpace(idToken))
            {
                requestUri += $"?idTokenHint={Uri.EscapeDataString(idToken)}";
            }

            var response = await _http.GetAsync(requestUri);
            if (response.IsSuccessStatusCode)
            {
                var logout = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.AdminAuthLogoutUrlResponse);
                if (!string.IsNullOrWhiteSpace(logout?.LogoutUrl))
                {
                    _nav.NavigateTo(logout.LogoutUrl, forceLoad: true);
                    return;
                }
            }
        }
        catch (HttpRequestException)
        {
        }

        NavigateToLocalLogin();
    }

    private void NavigateToLocalLogin()
    {
        _nav.NavigateTo("/admin/auth/login", forceLoad: true);
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
