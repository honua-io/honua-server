// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Authentication;

internal static class AdminAuthSessionTokenBridgeExtensions
{
    private const string SessionBridgedItemKey = "Honua.AdminAuth.SessionBridged";

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
