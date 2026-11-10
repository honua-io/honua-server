// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Caching;

namespace Honua.Infrastructure.SharedCaching;

// Runs when the response starts and has the last word: a response that is not provably public leaves as private, no-store.
internal static class SharedCacheGuard
{
    internal const string NotShared = "private, no-store";

    internal static void Apply(HttpContext context)
    {
        var response = context.Response;
        if (response.StatusCode == StatusCodes.Status101SwitchingProtocols)
        {
            return;
        }

        var cacheControl = response.Headers.CacheControl.ToString();
        if (ExcludesSharedCaches(cacheControl))
        {
            return;
        }

        if (cacheControl.Length > 0 && ResponseMayBeShared(context))
        {
            return;
        }

        response.Headers.CacheControl = NotShared;
    }

    internal static bool ExcludesSharedCaches(string cacheControl)
        => cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase)
           || cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase);

    internal static bool RequestMayBeShared(HttpContext context)
        => (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
           && RequestAnonymity.IsAnonymous(context);

    private static bool ResponseMayBeShared(HttpContext context)
        => SharedCacheResponse.DeclaredClass(context) is not null
           && RequestMayBeShared(context)
           && IsCacheableStatus(context.Response.StatusCode)
           && !ResponseAccessVerdict.Restricted(context)
           && !BypassOutputCacheOnErrorEnvelopePolicy.TryGetErrorEnvelopeCode(context, out _)
           && !context.Response.Headers.ContainsKey("Set-Cookie");

    // An outage or a transient refusal is never cached: the CDN would keep serving it.
    private static bool IsCacheableStatus(int statusCode)
        => statusCode is StatusCodes.Status200OK
            or StatusCodes.Status204NoContent
            or StatusCodes.Status206PartialContent
            or StatusCodes.Status304NotModified;
}
