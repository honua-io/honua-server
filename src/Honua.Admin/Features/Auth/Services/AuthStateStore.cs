// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.JSInterop;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Tracks non-sensitive admin UI flow state in browser session storage.
/// </summary>
public sealed class AuthStateStore
{
    private const string ProviderKey = "honua_admin_provider";

    private readonly IJSRuntime _js;

    public AuthStateStore(IJSRuntime js)
    {
        _js = js;
    }

    public async Task StoreSelectedProviderKeyAsync(string providerKey) =>
        await SetItemAsync(ProviderKey, providerKey);

    public async Task<string?> GetProviderKeyAsync() => await GetItemAsync(ProviderKey);

    /// <summary>
    /// Clears all client-side auth state.
    /// </summary>
    public async Task ClearAsync()
    {
        await RemoveItemAsync(ProviderKey);
    }

    private async Task SetItemAsync(string key, string value) =>
        await _js.InvokeVoidAsync("sessionStorage.setItem", key, value);

    private async Task<string?> GetItemAsync(string key) =>
        await _js.InvokeAsync<string?>("sessionStorage.getItem", key);

    private async Task RemoveItemAsync(string key) =>
        await _js.InvokeVoidAsync("sessionStorage.removeItem", key);
}
