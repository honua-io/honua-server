// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Caching;
using Microsoft.Net.Http.Headers;

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

        // An origin never states an age; a replayed stored response would, and every cache downstream would trust it.
        response.Headers.Remove(HeaderNames.Age);

        var cacheControl = response.Headers.CacheControl.ToString();
        // Every refusal leaves in one form: a refusal that also carries a lifetime contradicts itself, and caches resolve the contradiction differently.
        if (cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase))
        {
            LeaveAsNotShared(context);
            return;
        }

        if (cacheControl.Length > 0 && !ExcludesSharedCaches(cacheControl) && ResponseMayBeShared(context))
        {
            return;
        }

        if (ExcludesSharedCaches(cacheControl))
        {
            // The ETag service writes this pair only when nothing else set a policy. It is a
            // validation fallback, not a decision to store the response in a private cache.
            // A response that is not shareable still leaves as private, no-store. A handler
            // that chose its own private lifetime is left alone.
            if (IsETagFallback(cacheControl) && !ResponseMayBeShared(context))
            {
                LeaveAsNotShared(context);
                return;
            }

            SharedCacheResponse.RemoveCacheTags(context);
            return;
        }

        LeaveAsNotShared(context);
    }

    // CacheControlHeaderValue reorders directives, so compare the set rather than the raw string.
    private static bool IsETagFallback(string cacheControl)
    {
        var directives = cacheControl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return directives.Length == 2
            && directives.Contains("private", StringComparer.OrdinalIgnoreCase)
            && directives.Contains("must-revalidate", StringComparer.OrdinalIgnoreCase);
    }

    private static void LeaveAsNotShared(HttpContext context)
    {
        context.Response.Headers.CacheControl = NotShared;
        SharedCacheResponse.RemoveCacheTags(context);
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
