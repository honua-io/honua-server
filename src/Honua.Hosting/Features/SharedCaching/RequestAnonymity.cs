// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Caching;

namespace Honua.Infrastructure.SharedCaching;

internal static class RequestAnonymity
{
    // A development-bypass identity counts as authenticated here: when unsure, the response is not shared.
    internal static bool IsAnonymous(HttpContext context)
        => !BypassOutputCacheOnCredentialedRequestPolicy.CarriesCredential(context)
           && !context.User.Identities.Any(static identity => identity.IsAuthenticated || identity.Claims.Any());
}
