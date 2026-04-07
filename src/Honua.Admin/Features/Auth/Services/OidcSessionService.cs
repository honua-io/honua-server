// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Admin.Features.Auth.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Manages the backend-assisted OIDC authorization-code flow and logout.
/// </summary>
public sealed class OidcSessionService
{
    private const string AuthorizationCodeGrantType = "authorization_code";
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
    /// Initiates the authorization code flow by requesting an authorize URL from the server.
    /// </summary>
    public async Task StartLoginAsync(AuthProviderInfo provider)
    {
        await _store.ClearAsync();
        await _store.StoreSelectedProviderKeyAsync(provider.Key);

        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/admin/auth/providers/{Uri.EscapeDataString(provider.Key)}/authorize-url");
        request.Content = JsonContent.Create(
            new AdminAuthAuthorizeUrlRequest(),
            AuthJsonContext.Default.AdminAuthAuthorizeUrlRequest);

        using var response = await _http.SendAsync(request);

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
    /// Handles the callback from the OIDC provider and exchanges the code for tokens.
    /// Returns true on success.
    /// </summary>
    public async Task<(bool Success, string? Error)> HandleCallbackAsync(string code, string state, AuthProviderInfo provider)
    {
        try
        {
            using var request = CreateRequest(
                HttpMethod.Post,
                $"api/v1/admin/auth/providers/{Uri.EscapeDataString(provider.Key)}/token");
            request.Content = JsonContent.Create(
                new AdminAuthTokenRequest
                {
                    GrantType = AuthorizationCodeGrantType,
                    Code = code,
                    State = state
                },
                AuthJsonContext.Default.AdminAuthTokenRequest);

            using var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return (false, TokenExchangeFailureMessage);
            }

            await _store.ClearAsync();
            var session = await GetCurrentSessionAsync();
            if (!session.IsAuthenticated)
            {
                return (false, TokenExchangeFailureMessage);
            }

            return (true, null);
        }
        catch (HttpRequestException)
        {
            return (false, TokenExchangeUnavailableMessage);
        }
    }

    /// <summary>
    /// Silent refresh is intentionally disabled for the hosted admin UI.
    /// Expiring sessions re-authenticate through the identity provider.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Instance member retained for service API consistency.")]
    public Task<bool> TryRefreshAsync() => Task.FromResult(false);

    /// <summary>
    /// Returns true while the current access token is still valid.
    /// Silent refresh is disabled, so expiry requires a new interactive sign-in.
    /// </summary>
    public async Task<bool> EnsureValidSessionAsync()
    {
        var session = await GetCurrentSessionAsync();
        return session.IsAuthenticated &&
               session.ExpiresAt is DateTimeOffset expiresAt &&
               expiresAt > DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns the current hosted admin session state from the server.
    /// </summary>
    public async Task<AdminAuthSessionInfo> GetCurrentSessionAsync()
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "api/v1/admin/auth/session");
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return new AdminAuthSessionInfo();
            }

            return await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.AdminAuthSessionInfo)
                ?? new AdminAuthSessionInfo();
        }
        catch (HttpRequestException)
        {
            return new AdminAuthSessionInfo();
        }
    }

    /// <summary>
    /// Clears local flow state and redirects to a provider logout URL when available.
    /// </summary>
    public async Task LogoutAsync()
    {
        await _store.ClearAsync();

        try
        {
            using var request = CreateRequest(HttpMethod.Post, "api/v1/admin/auth/logout");
            using var response = await _http.SendAsync(request);
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

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return request;
    }
}
