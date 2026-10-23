// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Output cache policy that stores the deterministic NON-200 outcomes of a tile request —
/// <c>204 No Content</c> for a tile the layer has no features in, and
/// <c>413 Payload Too Large</c> for a tile whose encoded MVT exceeds
/// <c>Limits:Tiles:MaxTileSize</c>.
/// </summary>
/// <remarks>
/// ASP.NET Core's default output-cache policy stores 200 responses only, so without this hook
/// every empty or over-budget tile re-runs the full <c>ST_AsMVT</c> encode on every request even
/// though the answer is a pure function of the cache key (tenant, licence, route, tile matrix,
/// tile coordinates, query/Accept variants and the enforced byte budget). That is the whole
/// working set for the two most common tile responses a real deployment serves: a sparse layer
/// answers 204 for nearly every tile, and a layer that is dense at low zoom answers 413 for the
/// handful of tiles that carry its features. honua-server#4918 measured the cost — the 2026.1
/// capacity envelope's 10,000-feature layer encodes a 1,492,055-byte z0 tile in ~1.4 s, which the
/// server then discarded with a 413 on every one of 22,853 requests.
/// <para>
/// Only the tile policies that already partition their cache key by the enforced byte budget opt
/// into this, so an entry can never outlive the limit that produced it: raising or lowering
/// <c>Limits:Tiles:MaxTileSize</c> moves every tile to a different key. Staleness after a data
/// change is bounded by the same TTL that already governs cached 200 tiles.
/// </para>
/// </remarks>
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

    /// <summary>
    /// The GeoServices tile routes carry the over-budget refusal as an Esri error envelope with
    /// body code 413 over HTTP 200. The shared base policy refuses storage for every error
    /// envelope (honua-server#4980); this refusal is the one deliberate exception, because it is
    /// as deterministic for the budget-partitioned key as the OGC routes' real 413.
    /// </summary>
    private static bool IsOverBudgetEnvelope(HttpContext httpContext)
        => httpContext.Response.StatusCode == StatusCodes.Status200OK
            && BypassOutputCacheOnErrorEnvelopePolicy.TryGetErrorEnvelopeCode(httpContext, out var code)
            && code == StatusCodes.Status413PayloadTooLarge;
}
