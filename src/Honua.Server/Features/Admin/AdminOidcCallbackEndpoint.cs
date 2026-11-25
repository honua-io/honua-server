// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal sealed partial class AdminOidcCallbackEndpoint(
    AdminAuthSessionStore sessionStore,
    IOptions<OidcAuthenticationOptions> oidcOptions,
    ILogger<AdminAuthEndpoints.AdminAuthEndpointsLog> logger)
{
    internal const string DefaultReturnPath = "/console/";
    internal const string SignInPath = "/console/auth/signin";
    private const string AuthRoutePrefix = "/console/auth/";
    private const int MaxReturnPathLength = 256;
    private const int MaxLoggedDescriptionLength = 200;

    internal static bool IsAllowedReturnPath(string? path) =>
        path is not null
        && path.Length <= MaxReturnPathLength
        && !path.StartsWith(AuthRoutePrefix, StringComparison.OrdinalIgnoreCase)
        && AllowedReturnPath().IsMatch(path);

    internal async Task<IResult> HandleAsync(HttpContext context, IHttpClientFactory clients)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps)
        {
            return TypedResults.BadRequest();
        }

        var pendingId = context.Request.Cookies[AdminAuthSessionStore.PendingSessionCookieName];
        var pending = await sessionStore.GetPendingSessionAsync(pendingId, context.RequestAborted).ConfigureAwait(false);
        if (context.Request.Query.ContainsKey("error"))
        {
            return await RejectProviderErrorAsync(context, pendingId, pending).ConfigureAwait(false);
        }

        if (pending is null || !IsAllowedReturnPath(pending.ReturnTo))
        {
            return FailurePage("The sign-in attempt expired or was started in another browser.", errorCode: null);
        }

        var request = new AdminAuthTokenRequest
        {
            GrantType = "authorization_code",
            Code = context.Request.Query["code"].ToString(),
            State = context.Request.Query["state"].ToString()
        };
        var result = await AdminAuthEndpoints.HandleRequestToken(
            pending.ProviderKey, request, context, oidcOptions, sessionStore, clients, logger).ConfigureAwait(false);
        return result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent }
            ? TypedResults.LocalRedirect(pending.ReturnTo)
            : result;
    }

    // The identity provider returns the browser here after sign-out (Public:BaseUrl + /admin is the registered
    // post-logout URL). It lands on the Console sign-in page, on the same origin a completed sign-in returns to.
    internal IResult SignedOutLanding(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return oidcOptions.Value.TryGetAdminAuthCallbackUri(out var callback) && callback is not null
            ? TypedResults.Redirect(callback.GetLeftPart(UriPartial.Authority) + SignInPath)
            : TypedResults.LocalRedirect(SignInPath);
    }

    // The provider's error code is shown only when it is a plain token; its free-text description is logged, never rendered.
    // Only an error carrying the attempt's own state ends that attempt; an error link with another state leaves it alone.
    private async Task<IResult> RejectProviderErrorAsync(HttpContext context, string? pendingId, AdminAuthPendingSession? pending)
    {
        var rawCode = context.Request.Query["error"].ToString();
        var errorCode = OAuthErrorCode().IsMatch(rawCode) ? rawCode : "unrecognized_error";
        var ownAttempt = pending is not null
            && PortalOAuthBroker.CryptographicEquals(pending.State, context.Request.Query["state"].ToString());
        AdminAuthLog.ProviderReturnedError(
            logger, ownAttempt ? pending!.ProviderKey : "unknown", ForLog(rawCode),
            ForLog(context.Request.Query["error_description"].ToString()));
        if (ownAttempt)
        {
            await sessionStore.RemovePendingSessionAsync(pendingId, context.RequestAborted).ConfigureAwait(false);
            AdminAuthEndpoints.DeleteCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName);
        }

        return FailurePage("The identity provider refused the sign-in request.", errorCode);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult FailurePage(string reason, string? errorCode)
    {
        var detail = errorCode is null
            ? string.Empty
            : $"<p>Identity provider error: <code>{System.Net.WebUtility.HtmlEncode(errorCode)}</code>. "
                + "An administrator can find the provider's description in the server log.</p>";
        var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Sign-in failed</title></head>"
            + $"<body><h1>Sign-in failed</h1><p>{System.Net.WebUtility.HtmlEncode(reason)}</p>{detail}"
            + $"<p><a href=\"{SignInPath}\">Try again</a></p></body></html>";
        return TypedResults.Content(html, "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
    }

    // Keeps letters, digits, punctuation, symbols and plain spaces, so provider text cannot split or disguise log lines.
    private static string ForLog(string value)
    {
        var printable = new string(value.Where(IsLoggable).Take(MaxLoggedDescriptionLength).ToArray());
        return printable.Length == 0 ? "(none)" : printable;
    }

    private static bool IsLoggable(char c) => c == ' ' || (!char.IsSurrogate(c) && char.GetUnicodeCategory(c) is not (
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator or UnicodeCategory.PrivateUse
        or UnicodeCategory.OtherNotAssigned));

    [GeneratedRegex(@"^/console/(?:[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedReturnPath();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex OAuthErrorCode();
}
