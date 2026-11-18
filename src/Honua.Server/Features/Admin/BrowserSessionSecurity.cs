// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Antiforgery;

namespace Honua.Server.Features.Admin;

internal static class BrowserSessionSecurity
{
    internal const string HeaderName = "X-Honua-CSRF";

    internal static IServiceCollection AddBrowserSessionSecurity(this IServiceCollection services)
    {
        services.AddScoped<AdminPasswordAuthenticator>();
        services.AddScoped<AdminPasswordSessionEndpoint>();
        services.AddScoped<AdminOidcCallbackEndpoint>();
        services.AddScoped<AdminPasswordTokenEndpoint>();
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
                || (request.Cookies.ContainsKey(AdminAuthSessionStore.AuthSessionCookieName)
                    && (metadata?.GetMetadata<UsesBrowserSessionMetadata>() is not null
                        || !HasAuthenticatedExplicitCredential(context)));
            // A WebSocket upgrade is a GET, so the antiforgery check never sees it. When the browser
            // session cookie is the credential, the upgrade must come from this origin: same-site
            // pages on another origin (a sibling subdomain) would otherwise receive the cookie.
            if (context.WebSockets.IsWebSocketRequest
                && request.Cookies.ContainsKey(AdminAuthSessionStore.AuthSessionCookieName)
                && !HasAuthenticatedExplicitCredential(context)
                && !IsSameOrigin(request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // SAML POST bindings validate signed protocol messages and their own correlation state.
            if (!safe && requiresCsrf && metadata?.GetMetadata<BrowserCsrfProtocolBindingMetadata>() is null)
            {
                if (!request.IsHttps || !IsSameOrigin(request))
                {
                    await RejectCsrfAsync(context).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>()
                        .ValidateRequestAsync(context).ConfigureAwait(false);
                }
                catch (AntiforgeryValidationException)
                {
                    await RejectCsrfAsync(context).ConfigureAwait(false);
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool HasAuthenticatedExplicitCredential(HttpContext context) =>
        context.User.Identity is { IsAuthenticated: true } identity
        && identity.AuthenticationType != OidcAuthenticationExtensions.AdminSessionScheme
        && !context.IsAdminAuthSessionBridged()
        && (context.Request.Headers.ContainsKey("X-API-Key")
            || context.Request.Headers.ContainsKey("Authorization"));

    private const string RefusalReason =
        $"Browser session requests require same-origin HTTPS, the session's antiforgery cookie and a fresh {HeaderName} token.";

    // The refusal names its reason so a Console, or a proxy that drops the antiforgery cookie, is diagnosable.
    // It stays HTTP 400 on every path, including GeoServices routes whose errors are otherwise HTTP 200.
    private static async Task RejectCsrfAsync(HttpContext context)
    {
        if (context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/grpc";
            context.Response.Headers["grpc-status"] = "7";
            context.Response.Headers["grpc-message"] = Uri.EscapeDataString(RefusalReason);
            return;
        }

        if (ProtocolRequestClassifier.IsWmsAlias(context.Request.Path))
        {
            // WMS errors are otherwise HTTP 200 service exception reports; this refusal stays HTTP 400.
            await Results.Text(RefusalReason, "text/plain", statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await StandardErrorResponseFormatter.FormatError(
                context,
                StandardErrorResponse.BadRequest(RefusalReason),
                new ErrorResponseFormatterOptions { GeoServicesUseHttpStatus = true })
            .ExecuteAsync(context).ConfigureAwait(false);
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

internal sealed class UsesBrowserSessionMetadata;

internal sealed class BrowserCsrfProtocolBindingMetadata;
