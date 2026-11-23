// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Honua.Infrastructure.Authentication;

// Browser sign-in goes through the admin OIDC broker, never through the OpenIdConnect
// handlers, but each handler still answers its own paths. These callbacks keep those
// paths from failing with a 500.
internal static class OidcHandlerCallbacks
{
    // Front-channel logout (RemoteSignOutPath, /signout-oidc). No handler ticket exists to
    // end; left to the default, the handler signs out of the composite policy scheme, which
    // forwards to a scheme that cannot sign out. A no-op 200 that is never cached is the
    // front-channel contract when no session matches.
    internal static Task CompleteRemoteSignOutAsync(RemoteSignOutContext context)
    {
        WriteUncachedStatus(context.Response, StatusCodes.Status200OK);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    // Sign-in callback (CallbackPath, /signin-oidc*) without a valid response. Left to the
    // default, the handler throws and the caller gets a 500; the request is the caller's fault.
    internal static Task RefuseRemoteFailureAsync(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
        OidcAuthenticationLog.OidcRemoteCallbackRefused(
            logger, context.Scheme.Name, context.Failure?.Message ?? "unknown failure");
        WriteUncachedStatus(context.Response, StatusCodes.Status400BadRequest);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static void WriteUncachedStatus(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Pragma = "no-cache";
    }
}
