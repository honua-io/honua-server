// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;

namespace Honua.Infrastructure.Authentication;

// Answers the OpenID Connect front-channel logout path (RemoteSignOutPath, /signout-oidc).
// Browser sign-in goes through the admin OIDC broker, never through these handlers, so no
// ticket from them exists to end. Left to the default, the handler signs out of the
// composite policy scheme, which forwards to a scheme that cannot sign out, and the
// IdP's logout iframe gets a 500. A no-op 200 that is never cached is the default contract.
internal static class OidcRemoteSignOut
{
    internal static Task CompleteWithoutSessionAsync(RemoteSignOutContext context)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Pragma = "no-cache";
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
