// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Caching;
using Microsoft.Extensions.DependencyInjection;

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
           && IsCacheableStatus(context)
           && !ResponseAccessVerdict.Restricted(context)
           && !BypassOutputCacheOnErrorEnvelopePolicy.TryGetErrorEnvelopeCode(context, out _)
           && !context.Response.Headers.ContainsKey("Set-Cookie")
           && (SharedCacheResponse.IsGenerationAddressed(context) || !RolloutWindowIsOpen(context));

    // An outage or a transient refusal is never cached. A missing tile at a generation address stays missing for that generation.
    private static bool IsCacheableStatus(HttpContext context)
        => context.Response.StatusCode switch
        {
            StatusCodes.Status200OK
                or StatusCodes.Status204NoContent
                or StatusCodes.Status206PartialContent
                or StatusCodes.Status304NotModified => true,
            StatusCodes.Status404NotFound => SharedCacheResponse.IsGenerationAddressed(context),
            _ => false,
        };

    private static bool RolloutWindowIsOpen(HttpContext context)
        => context.RequestServices.GetService<IRolloutWindow>()?.IsOpen == true;
}
