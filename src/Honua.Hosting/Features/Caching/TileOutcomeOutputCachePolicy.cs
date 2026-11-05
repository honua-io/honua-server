// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

// Only a policy whose cache key includes the byte budget may store an empty or over-budget tile, so the entry cannot outlive the limit that produced it.
internal sealed class TileOutcomeOutputCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (context.AllowCacheStorage)
        {
            return ValueTask.CompletedTask;
        }

        var httpContext = context.HttpContext;
        var response = httpContext.Response;
        if (response.StatusCode is not (StatusCodes.Status204NoContent or StatusCodes.Status413PayloadTooLarge)
            && !IsOverBudgetEnvelope(httpContext))
        {
            return ValueTask.CompletedTask;
        }

        // Re-assert every reason the default and Honua-specific policies refuse storage, because
        // this policy runs last and its decision is the one the middleware acts on: a response
        // that sets a cookie, belongs to an authenticated principal, answers a request presenting an
        // application-defined credential, or declares itself no-store must stay out of a shared
        // cache whatever its status code is.
        if (!StringValues.IsNullOrEmpty(response.Headers.SetCookie)
            || httpContext.User?.Identity?.IsAuthenticated == true
            || BypassOutputCacheOnCredentialedRequestPolicy.CarriesCredential(httpContext)
            || response.Headers[HeaderNames.CacheControl].ToString()
                .Contains("no-store", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.CompletedTask;
        }

        context.AllowCacheStorage = true;
        return ValueTask.CompletedTask;
    }

    // The GeoServices HTTP 200 envelope whose body code is 413 is the one error envelope stored here, because the cache key already includes the byte budget.
    private static bool IsOverBudgetEnvelope(HttpContext httpContext)
        => httpContext.Response.StatusCode == StatusCodes.Status200OK
            && BypassOutputCacheOnErrorEnvelopePolicy.TryGetErrorEnvelopeCode(httpContext, out var code)
            && code == StatusCodes.Status413PayloadTooLarge;
}
