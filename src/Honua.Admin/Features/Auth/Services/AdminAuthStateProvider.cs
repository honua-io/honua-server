// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Custom AuthenticationStateProvider that reads tokens from sessionStorage
/// and provides claims-based identity for Blazor authorization.
/// </summary>
public sealed class AdminAuthStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState AnonymousState = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly AuthStateStore _store;
    private readonly OidcSessionService _session;

    public AdminAuthStateProvider(AuthStateStore store, OidcSessionService session)
    {
        _store = store;
        _session = session;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var accessToken = await _store.GetAccessTokenAsync();

        if (string.IsNullOrEmpty(accessToken))
            return AnonymousState;

        // Check token expiry and try refresh if needed
        if (await _store.IsTokenExpiredAsync())
        {
            var refreshed = await _session.TryRefreshAsync();
            if (!refreshed)
            {
                return AnonymousState;
            }

            accessToken = await _store.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return AnonymousState;
        }

        var claims = ParseClaimsFromJwt(accessToken);
        var identity = new ClaimsIdentity(claims, "oidc");
        var principal = new ClaimsPrincipal(identity);

        return new AuthenticationState(principal);
    }

    /// <summary>
    /// Notifies the Blazor authorization system that the auth state has changed.
    /// Call after login, logout, or token refresh.
    /// </summary>
    public void NotifyAuthStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    private static IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(jwt);
            return token.Claims;
        }
        catch
        {
            // If token parsing fails, return empty claims rather than crashing the UI
            return [];
        }
    }
}
