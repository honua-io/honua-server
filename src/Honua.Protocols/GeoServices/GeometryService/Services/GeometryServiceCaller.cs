// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// Most geometry routes are open to callers that are not signed in; their limits are lower.
internal enum GeometryServiceCaller
{
    NotSignedIn,
    SignedIn,
}

internal static class GeometryServiceCallers
{
    public static GeometryServiceCaller Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.User.Identities.Any(static identity => identity.IsAuthenticated)
            ? GeometryServiceCaller.SignedIn
            : GeometryServiceCaller.NotSignedIn;
    }
}
