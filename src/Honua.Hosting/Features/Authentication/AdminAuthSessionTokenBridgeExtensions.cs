// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal static class AdminAuthSessionTokenBridgeExtensions
{
    private const string SessionBridgedItemKey = "Honua.AdminAuth.SessionBridged";

    public static IApplicationBuilder UseAdminAuthSessionTokenBridge(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(HeaderNames.Authorization) ||
                !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (!context.Request.Cookies.TryGetValue(AdminAuthSessionStore.AuthSessionCookieName, out var sessionId) ||
                string.IsNullOrWhiteSpace(sessionId))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var sessionStore = context.RequestServices.GetRequiredService<AdminAuthSessionStore>();
            var session = await sessionStore.GetAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            if (session is null || string.IsNullOrWhiteSpace(session.AccessToken))
            {
                await sessionStore.RemoveAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
                DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
                await next().ConfigureAwait(false);
                return;
            }

            context.Items[SessionBridgedItemKey] = true;
            context.Request.Headers.Authorization = $"Bearer {session.AccessToken}";

            await next().ConfigureAwait(false);
        });
    }

    public static bool IsAdminAuthSessionBridged(this HttpContext context)
    {
        return context.Items.ContainsKey(SessionBridgedItemKey);
    }

    private static void DeleteCookie(HttpResponse response, string name)
    {
        response.Cookies.Delete(name, new CookieOptions
        {
            Path = "/"
        });
    }
}
