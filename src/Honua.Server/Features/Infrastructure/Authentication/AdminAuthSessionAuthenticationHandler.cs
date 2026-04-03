// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal sealed class AdminAuthSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminAuthSessionStore sessionStore) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private readonly AdminAuthSessionStore _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(AdminAuthSessionStore.AuthSessionCookieName, out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await _sessionStore.GetAuthenticatedSessionAsync(sessionId, Context.RequestAborted).ConfigureAwait(false);
        if (session is null || session.Claims is null || session.Claims.Length == 0)
        {
            await _sessionStore.RemoveAuthenticatedSessionAsync(sessionId, Context.RequestAborted).ConfigureAwait(false);
            DeleteSessionCookie();
            return AuthenticateResult.NoResult();
        }

        try
        {
            var principal = AdminAuthClaimsProjector.CreatePrincipal(session.Claims, Scheme.Name);
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (ArgumentException ex)
        {
            Logger.LogWarning(ex, "Failed to project the server-managed admin session into an authenticated principal.");
            await _sessionStore.RemoveAuthenticatedSessionAsync(sessionId, Context.RequestAborted).ConfigureAwait(false);
            DeleteSessionCookie();
            return AuthenticateResult.Fail("The admin session is invalid or expired.");
        }
    }

    private void DeleteSessionCookie()
    {
        Response.Cookies.Delete(AdminAuthSessionStore.AuthSessionCookieName, new CookieOptions
        {
            Path = "/"
        });
    }
}
