// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
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
    private static readonly object AdHocSpatialKey = new();

    internal static void UseLifetime(HttpContext context, SharedCacheLifetime lifetime)
        => context.Items[LifetimeKey] = lifetime;

    internal static void UseSubject(HttpContext context, SharedCacheSubject subject)
        => context.Items[SubjectKey] = subject;

    // A publication resolved without its service leaves the subject to the route.
    internal static void UsePublication(HttpContext context, MetadataV2Service? service, MetadataV2Layer publication)
    {
        if (service is not null)
        {
            UseSubject(context, SharedCacheSubject.ForPublication(service, publication));
        }
    }

    // Arbitrary extents and spatial predicates make a near-unique address per request, which must not be cached exactly.
    internal static bool ExcludeIfAdHocSpatial(HttpContext context, FeatureQuery query, string? filterText)
    {
        if (!ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(query, filterText))
        {
            return false;
        }

        context.Items[AdHocSpatialKey] = true;
        return true;
    }

    internal static SharedCacheSubject? Subject(HttpContext context)
        => context.Items.TryGetValue(SubjectKey, out var subject) && subject is SharedCacheSubject declared
            ? declared
            : SharedCacheSubject.FromRoute(context.Request.RouteValues);

    // The tileset key only selects a configured tileset lifetime; the subject comes from the publication that granted access.
    internal static void UseTileset(HttpContext context, TileOptions tileOptions, TilesetAddress tileset)
    {
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
            || context.Items.ContainsKey(AdHocSpatialKey)
            || HandlerForbadeStorage(context.Response))
        {
            return;
        }

        var options = context.RequestServices.GetService<IOptionsMonitor<SharedCacheOptions>>()?.CurrentValue ?? ProductDefaults;
        context.Response.Headers.CacheControl = FormatCacheControl(context, cacheClass, options);
        AddMissingVary(context.Response, VaryFor(cacheClass));
        if (options.CacheTagsEnabled && CdnCacheTags.ForResponse(Subject(context), context.Request.Path, context.Request.RouteValues["serviceId"]?.ToString())
                is { Length: > 0 } tags)
        {
            context.Response.Headers[options.CacheTagHeader!] = string.Join(',', tags);
        }
    }

    // A response that leaves as not shared must not name what it is about to a cache that will not store it.
    internal static void RemoveCacheTags(HttpContext context)
    {
        if (context.RequestServices?.GetService<IOptionsMonitor<SharedCacheOptions>>()?.CurrentValue is { CacheTagsEnabled: true } options)
        {
            context.Response.Headers.Remove(options.CacheTagHeader!);
        }
    }

    // The lifetime the CDN is given for this response, whether or not this request turns out to qualify for sharing.
    internal static TimeSpan? SharedLifetime(HttpContext context)
    {
        if (DeclaredClass(context) is not { } cacheClass)
        {
            return null;
        }

        var options = context.RequestServices?.GetService<IOptionsMonitor<SharedCacheOptions>>()?.CurrentValue ?? ProductDefaults;
        return TimeSpan.FromSeconds(Math.Max(0, ResolveLifetime(context, cacheClass, options).SharedMaxAgeSeconds));
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
        var directives = new List<string>
        {
            "public",
            $"max-age={Seconds(lifetime.MaxAgeSeconds)}",
            $"s-maxage={Seconds(lifetime.SharedMaxAgeSeconds)}",
        };

        if (lifetime.StaleWhileRevalidateSeconds > 0)
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
        var subject = Subject(context);
        if (options.Emergency.AppliesTo(subject))
        {
            return SharedCacheEmergency.Lifetime;
        }

        if (subject?.LayerKey is { } layerKey && options.Layers.TryGetValue(layerKey, out var configuredForLayer))
        {
            return configuredForLayer;
        }

        return context.Items.TryGetValue(LifetimeKey, out var declared) && declared is SharedCacheLifetime layerLifetime
            ? layerLifetime
            : options.DefaultFor(cacheClass);
    }

    private static string Seconds(int seconds)
        => Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture);

    private static void AddMissingVary(HttpResponse response, string[] requestHeaders)
    {
        var present = response.Headers.Vary.ToString();
        foreach (var requestHeader in requestHeaders.Where(header => !present.Contains(header, StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers.Append(HeaderNames.Vary, requestHeader);
        }
    }

    // Origin is always listed: the CORS policy reflects the request origin, and only on requests that send one.
    // A tile without an explicit format is PNG or vector by Accept.
    private static string[] VaryFor(ResponseCacheClass cacheClass) => cacheClass switch
    {
        ResponseCacheClass.Tile => [HeaderNames.Origin, HeaderNames.Accept, HeaderNames.AcceptEncoding],
        ResponseCacheClass.FeatureQuery => [HeaderNames.Origin, HeaderNames.Accept, HeaderNames.AcceptEncoding, "Prefer"],
        ResponseCacheClass.MetadataDocument =>
            [HeaderNames.Origin, HeaderNames.Accept, HeaderNames.AcceptEncoding, HeaderNames.AcceptLanguage],
        _ => [HeaderNames.Origin, HeaderNames.AcceptEncoding],
    };
}

// What a response is about. Lifetimes and the emergency setting are chosen by it today; a cache tag can be derived from it without touching a call site.
internal sealed record SharedCacheSubject(string ServiceId, string? LayerId = null, string? StyleId = null)
{
    private static readonly string[] ServiceRouteValues = ["serviceId", "collectionId", "sceneId", "datasetId"];

    internal string? LayerKey => LayerId is null ? null : $"{ServiceId}/{LayerId}";

    // The same service and layer an operator sees in /rest/services/{serviceId}/FeatureServer/{layerId}, whichever surface serves the response.
    internal static SharedCacheSubject ForPublication(MetadataV2Service service, MetadataV2Layer publication)
        => new(service.EffectivePublicName, publication.LayerIndex?.ToString(CultureInfo.InvariantCulture));

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
