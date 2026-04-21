// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Honua.Admin.Features.Auth.Services;

/// <summary>
/// Custom AuthenticationStateProvider that projects the server-managed admin session
/// into Blazor authorization state.
/// </summary>
public sealed class AdminAuthStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState AnonymousState = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly OidcSessionService _sessionService;

    public AdminAuthStateProvider(OidcSessionService sessionService)
    {
        _sessionService = sessionService;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var session = await _sessionService.GetCurrentSessionAsync().ConfigureAwait(false);
        if (!session.IsAuthenticated)
        {
            return AnonymousState;
        }

        var claims = session.Claims.Select(claim => new Claim(claim.Type, claim.Value));
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
}
