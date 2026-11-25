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

        var excludesShared = cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase);
        if (cacheControl.Length > 0 && !excludesShared && ResponseMayBeShared(context))
        {
            return;
        }

        if (excludesShared)
        {
            // The ETag service writes this pair only when nothing else set a policy. It is a
            // validation fallback, not a decision to store the response in a private cache.
            // A response that is not shareable still leaves as private, no-store. A handler
            // that chose its own private lifetime is left alone.
            if (IsETagFallback(cacheControl.AsSpan()) && !ResponseMayBeShared(context))
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
    private static bool IsETagFallback(ReadOnlySpan<char> cacheControl)
    {
        var count = 0;
        var hasPrivate = false;
        var hasMustRevalidate = false;
        foreach (var range in cacheControl.Split(','))
        {
            var directive = cacheControl[range].Trim();
            if (directive.IsEmpty)
            {
                continue;
            }

            count++;
            if (count > 2)
            {
                return false;
            }

            if (directive.Equals("private", StringComparison.OrdinalIgnoreCase))
            {
                hasPrivate = true;
            }
            else if (directive.Equals("must-revalidate", StringComparison.OrdinalIgnoreCase))
            {
                hasMustRevalidate = true;
            }
            else
            {
                return false;
            }
        }

        return count == 2 && hasPrivate && hasMustRevalidate;
    }

    private static void LeaveAsNotShared(HttpContext context)
    {
        context.Response.Headers.CacheControl = NotShared;
        SharedCacheResponse.RemoveCacheTags(context);
    }

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
