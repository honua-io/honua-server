// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.JSInterop;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Manages auth session state in browser sessionStorage.
/// Token data lives only for the browser tab lifetime.
/// </summary>
public sealed class AuthStateStore
{
    private const string AccessTokenKey = "honua_admin_access_token";
    private const string IdTokenKey = "honua_admin_id_token";
    private const string RefreshTokenKey = "honua_admin_refresh_token";
    private const string ExpiresAtKey = "honua_admin_expires_at";
    private const string ProviderKey = "honua_admin_provider";
    private const string PkceVerifierKey = "honua_admin_pkce_verifier";
    private const string PkceStateKey = "honua_admin_pkce_state";

    private readonly IJSRuntime _js;

    public AuthStateStore(IJSRuntime js)
    {
        _js = js;
    }

    public async Task StoreTokensAsync(string accessToken, string? idToken, string? refreshToken, int expiresIn, string providerKey)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        await SetItemAsync(AccessTokenKey, accessToken);
        await SetItemAsync(ExpiresAtKey, expiresAt);
        await SetItemAsync(ProviderKey, providerKey);

        if (idToken is not null)
            await SetItemAsync(IdTokenKey, idToken);

        if (refreshToken is not null)
            await SetItemAsync(RefreshTokenKey, refreshToken);
    }

    public async Task StoreSelectedProviderKeyAsync(string providerKey) =>
        await SetItemAsync(ProviderKey, providerKey);

    public async Task<string?> GetAccessTokenAsync() => await GetItemAsync(AccessTokenKey);
    public async Task<string?> GetIdTokenAsync() => await GetItemAsync(IdTokenKey);
    public async Task<string?> GetRefreshTokenAsync() => await GetItemAsync(RefreshTokenKey);
    public async Task<string?> GetProviderKeyAsync() => await GetItemAsync(ProviderKey);

    public async Task<DateTimeOffset?> GetExpiresAtAsync()
    {
        var raw = await GetItemAsync(ExpiresAtKey);
        if (raw is not null && long.TryParse(raw, out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }

    public async Task<bool> IsTokenExpiredAsync()
    {
        var expiresAt = await GetExpiresAtAsync();
        return expiresAt is null || expiresAt <= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns true when the token will expire within the given buffer window.
    /// Used to trigger proactive refresh.
    /// </summary>
    public async Task<bool> IsTokenExpiringSoonAsync(TimeSpan buffer)
    {
        var expiresAt = await GetExpiresAtAsync();
        return expiresAt is null || expiresAt <= DateTimeOffset.UtcNow.Add(buffer);
    }

    public async Task StorePkceStateAsync(string verifier, string state)
    {
        await SetItemAsync(PkceVerifierKey, verifier);
        await SetItemAsync(PkceStateKey, state);
    }

    public async Task<(string? Verifier, string? State)> GetPkceStateAsync()
    {
        var verifier = await GetItemAsync(PkceVerifierKey);
        var state = await GetItemAsync(PkceStateKey);
        return (verifier, state);
    }

    public async Task ClearPkceStateAsync()
    {
        await RemoveItemAsync(PkceVerifierKey);
        await RemoveItemAsync(PkceStateKey);
    }

    /// <summary>
    /// Clears only token-related storage, preserving PKCE state for in-flight login flows.
    /// </summary>
    public async Task ClearTokensAsync()
    {
        await RemoveItemAsync(AccessTokenKey);
        await RemoveItemAsync(IdTokenKey);
        await RemoveItemAsync(RefreshTokenKey);
        await RemoveItemAsync(ExpiresAtKey);
        await RemoveItemAsync(ProviderKey);
    }

    /// <summary>
    /// Clears all auth state including tokens and PKCE state.
    /// </summary>
    public async Task ClearAsync()
    {
        await ClearTokensAsync();
        await ClearPkceStateAsync();
    }

    private async Task SetItemAsync(string key, string value) =>
        await _js.InvokeVoidAsync("sessionStorage.setItem", key, value);

    private async Task<string?> GetItemAsync(string key) =>
        await _js.InvokeAsync<string?>("sessionStorage.getItem", key);

    private async Task RemoveItemAsync(string key) =>
        await _js.InvokeVoidAsync("sessionStorage.removeItem", key);
}
