// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.MultiTenancy;

namespace Honua.Infrastructure.SharedCaching;

internal static class RequestAnonymity
{
    // Set by the CDN on requests whose credential it cannot put in its cache key.
    private const string EdgeCacheBypassCookie = "honua_cache_bypass";

    // A development-bypass identity counts as authenticated, and a tenant-scoped answer is not everyone's answer: when unsure, the response is not shared.
    internal static bool IsAnonymous(HttpContext context)
        => !BypassOutputCacheOnCredentialedRequestPolicy.CarriesCredential(context)
           && !ScopedJobAuthentication.HasCandidate(context)
           && !context.Request.Cookies.ContainsKey(EdgeCacheBypassCookie)
           && !context.User.Identities.Any(static identity => identity.IsAuthenticated || identity.Claims.Any())
           && !context.Request.Headers.ContainsKey(TenantContextOptions.TenantHeaderName)
           && TenantScopeHelpers.ResolveRequestTenantId(context) is null;
}
