// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal sealed class AdminOidcCallbackEndpoint(
    AdminAuthSessionStore sessionStore,
    IOptions<OidcAuthenticationOptions> oidcOptions,
    ILogger<AdminAuthEndpoints.AdminAuthEndpointsLog> logger)
{
    internal const string DefaultReturnPath = "/console/";

    internal static bool IsAllowedReturnPath(string? path) =>
        path is DefaultReturnPath or "/console/operate/connections";

    internal async Task<IResult> HandleAsync(HttpContext context, IHttpClientFactory clients)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps)
        {
            return TypedResults.BadRequest();
        }

        var pending = await sessionStore.GetPendingSessionAsync(
            context.Request.Cookies[AdminAuthSessionStore.PendingSessionCookieName], context.RequestAborted).ConfigureAwait(false);
        if (pending is null || !IsAllowedReturnPath(pending.ReturnTo)
            || context.Request.Query.ContainsKey("error"))
        {
            return TypedResults.BadRequest();
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
}
