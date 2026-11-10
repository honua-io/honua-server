// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Antiforgery;

namespace Honua.Server.Features.Admin;

internal static class BrowserSessionSecurity
{
    internal const string HeaderName = "X-Honua-CSRF";

    internal static IServiceCollection AddBrowserSessionSecurity(this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            options.Cookie.Name = "honua_admin_csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        return services;
    }

    internal static IApplicationBuilder UseBrowserSessionSecurity(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method);
            var metadata = context.GetEndpoint()?.Metadata;
            var requiresCsrf = metadata?.GetMetadata<RequireBrowserCsrfMetadata>() is not null
                || request.Cookies.ContainsKey(AdminAuthSessionStore.AuthSessionCookieName);
            // SAML POST bindings validate signed protocol messages and their own correlation state.
            if (!safe && requiresCsrf && metadata?.GetMetadata<BrowserCsrfProtocolBindingMetadata>() is null)
            {
                if (!request.IsHttps || !IsSameOrigin(request))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>()
                        .ValidateRequestAsync(context).ConfigureAwait(false);
                }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool IsSameOrigin(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && !string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.GetLeftPart(UriPartial.Authority),
                $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class RequireBrowserCsrfMetadata;

internal sealed class BrowserCsrfProtocolBindingMetadata;
