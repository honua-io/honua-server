// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.SharedCaching;

// The only writer of a shared-cacheable Cache-Control. SharedCacheGuard removes it again when the response starts if the request turns out not to qualify.
internal static class SharedCacheResponse
{
    private static readonly SharedCacheOptions ProductDefaults = new();
    private static readonly object LifetimeKey = new();
    private static readonly object SubjectKey = new();

    internal static void UseLifetime(HttpContext context, SharedCacheLifetime lifetime)
        => context.Items[LifetimeKey] = lifetime;

    internal static void UseSubject(HttpContext context, SharedCacheSubject subject)
        => context.Items[SubjectKey] = subject;

    internal static SharedCacheSubject? Subject(HttpContext context)
        => context.Items.TryGetValue(SubjectKey, out var subject) && subject is SharedCacheSubject declared
            ? declared
            : SharedCacheSubject.FromRoute(context.Request.RouteValues);

    internal static void UseTileset(HttpContext context, TileOptions tileOptions, TilesetAddress tileset)
    {
        UseSubject(context, new SharedCacheSubject(tileset.ServiceId, tileset.LayerId));
        if (TilesetTtlResolver.ResolveTilesetTtl(tileOptions, tileset.TilesetKey) is { } tilesetTtl)
        {
            UseLifetime(context, SharedCacheLifetime.ForBrowserAndCdn(tilesetTtl));
        }
    }

    // Handlers that write the body themselves call this before the first write; the endpoint filter covers the rest.
    internal static void Apply(HttpContext context)
    {
        if (context.Response.HasStarted
            || DeclaredClass(context) is not { } cacheClass
            || !SharedCacheGuard.RequestMayBeShared(context)
            || !AccessPermitsSharing(context, cacheClass)
            || HandlerForbadeStorage(context.Response))
        {
            return;
        }

        var options = context.RequestServices.GetService<IOptionsMonitor<SharedCacheOptions>>()?.CurrentValue ?? ProductDefaults;
        context.Response.Headers.CacheControl = FormatCacheControl(context, cacheClass, options);
        AddMissingVary(context.Response, VaryFor(cacheClass));
    }

    internal static ResponseCacheClass? DeclaredClass(HttpContext context)
        => context.GetEndpoint()?.Metadata.GetMetadata<SharedCacheClassMetadata>()?.CacheClass;

    // The ETag service's own default is private, must-revalidate: that is a fallback, not a refusal.
    private static bool HandlerForbadeStorage(HttpResponse response)
        => response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase);

    // A metadata document may list several resources and filters out what the caller cannot read, so it needs no single grant.
    private static bool AccessPermitsSharing(HttpContext context, ResponseCacheClass cacheClass)
        => cacheClass is ResponseCacheClass.MetadataDocument
            ? !ResponseAccessVerdict.Restricted(context)
            : ResponseAccessVerdict.GrantedToAnonymous(context);

    // s-maxage is always written: the CDN's own minimum is 0, so the origin header is the only lifetime it has.
    private static string FormatCacheControl(HttpContext context, ResponseCacheClass cacheClass, SharedCacheOptions options)
    {
        var lifetime = ResolveLifetime(context, cacheClass, options);
        var sharedMaxAge = DiffersBetweenReleases(cacheClass) && RolloutIsInProgress(context)
            ? 0
            : lifetime.SharedMaxAgeSeconds;
        var directives = new List<string>
        {
            "public",
            $"max-age={Seconds(lifetime.MaxAgeSeconds)}",
            $"s-maxage={Seconds(sharedMaxAge)}",
        };

        if (lifetime.StaleWhileRevalidateSeconds > 0 && sharedMaxAge > 0)
        {
            directives.Add($"stale-while-revalidate={Seconds(lifetime.StaleWhileRevalidateSeconds)}");
        }

        if (cacheClass is (ResponseCacheClass.Tile or ResponseCacheClass.FeatureQuery) && options.StaleIfErrorSeconds > 0)
        {
            directives.Add($"stale-if-error={Seconds(options.StaleIfErrorSeconds)}");
        }

        return string.Join(", ", directives);
    }

    private static SharedCacheLifetime ResolveLifetime(HttpContext context, ResponseCacheClass cacheClass, SharedCacheOptions options)
    {
        if (options.Emergency.AppliesTo(Subject(context)))
        {
            return SharedCacheEmergency.Lifetime;
        }

        return context.Items.TryGetValue(LifetimeKey, out var declared) && declared is SharedCacheLifetime layerLifetime
            ? layerLifetime
            : options.DefaultFor(cacheClass);
    }

    // Two releases can describe the same service differently while both take traffic.
    private static bool DiffersBetweenReleases(ResponseCacheClass cacheClass)
        => cacheClass is ResponseCacheClass.MetadataDocument;

    private static bool RolloutIsInProgress(HttpContext context)
        => context.RequestServices.GetService<IRolloutWindow>()?.IsOpen == true;

    private static string Seconds(int seconds)
        => Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture);

    private static void AddMissingVary(HttpResponse response, string[] requestHeaders)
    {
        var present = response.Headers.Vary.ToString();
        foreach (var requestHeader in requestHeaders)
        {
            if (!present.Contains(requestHeader, StringComparison.OrdinalIgnoreCase))
            {
                response.Headers.Append(HeaderNames.Vary, requestHeader);
            }
        }
    }

    private static string[] VaryFor(ResponseCacheClass cacheClass) => cacheClass switch
    {
        ResponseCacheClass.FeatureQuery => [HeaderNames.Accept, HeaderNames.AcceptEncoding, "Prefer"],
        ResponseCacheClass.MetadataDocument => [HeaderNames.Accept, HeaderNames.AcceptEncoding, HeaderNames.AcceptLanguage],
        _ => [HeaderNames.AcceptEncoding],
    };
}

// What a response is about. Lifetimes and the emergency setting are chosen by it today; a cache tag can be derived from it without touching a call site.
internal sealed record SharedCacheSubject(string ServiceId, string? LayerId = null, string? StyleId = null)
{
    private static readonly string[] ServiceRouteValues = ["serviceId", "collectionId", "sceneId", "datasetId"];

    internal string? LayerKey => LayerId is null ? null : $"{ServiceId}/{LayerId}";

    // Most routes name their service and layer in the address, so a handler only declares a subject when the route does not.
    internal static SharedCacheSubject? FromRoute(RouteValueDictionary routeValues)
    {
        var serviceId = ServiceRouteValues.Select(name => routeValues[name]?.ToString()).FirstOrDefault(value => value is not null);
        return serviceId is null
            ? null
            : new SharedCacheSubject(serviceId, routeValues["layerId"]?.ToString(), routeValues["styleId"]?.ToString());
    }
}

internal readonly record struct TilesetAddress(string ServiceId, string LayerId, string TileMatrixSetId)
{
    internal string TilesetKey => TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId);
}
