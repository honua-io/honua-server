// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.SharedCaching;

// The only writer of a shared-cacheable Cache-Control. SharedCacheGuard removes it again when the response starts if the request turns out not to qualify.
internal static class SharedCacheResponse
{
    private static readonly SharedCacheOptions ProductDefaults = new();
    private static readonly object ClassKey = new();
    private static readonly object LifetimeKey = new();
    private static readonly object SubjectKey = new();
    private static readonly object AdHocSpatialKey = new();
    private static readonly object ProcessCacheKey = new();
    private static readonly object PurgesReachCdnKey = new();
    private static readonly object PurgeBackedSentKey = new();

    // Multi-operation endpoints (such as OGC KVP WMS/WMTS) declare a base class on the route
    // and select the operation-specific class here once dispatched.
    internal static void UseClass(HttpContext context, ResponseCacheClass cacheClass)
        => context.Items[ClassKey] = cacheClass;

    internal static void DoNotShare(HttpContext context)
        => context.Response.Headers.CacheControl = SharedCacheGuard.NotShared;

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

    // A response replayed from this task's memory may predate an edit another task made, and that task's purge cannot
    // reach this task's memory. Only a response rendered from the data for this request may get a purge-backed lifetime.
    internal static void MarkServedFromProcessCache(HttpContext context)
        => context.Items[ProcessCacheKey] = true;

    // True when this response gets, or was sent with, a purge-backed CDN lifetime. No in-process cache may store such a
    // response: replaying it on this task after another task's edit would hand the CDN a stale copy for a day.
    internal static bool IsPurgeBacked(HttpContext context)
    {
        if (context.Items.ContainsKey(PurgeBackedSentKey))
        {
            return true;
        }

        if (DeclaredClass(context) is not { } cacheClass)
        {
            return false;
        }

        return ResolveLifetime(context, cacheClass, CurrentOptions(context)).PurgeBacked;
    }

    // Handlers that write the body themselves call this before the first write; the endpoint filter covers the rest.
    internal static void Apply(HttpContext context)
    {
        // The output cache captures its entry lifetime on the first body write, after the handler selects the subject.
        if (!context.Response.HasStarted && context.Features.Get<IOutputCacheFeature>() is { } outputCache)
        {
            CapOutputCacheLifetimeAtCdnLifetimePolicy.Apply(outputCache.Context);
        }

        if (context.Response.HasStarted
            || DeclaredClass(context) is not { } cacheClass
            || !SharedCacheGuard.RequestMayBeShared(context)
            || !AccessPermitsSharing(context, cacheClass)
            || context.Items.ContainsKey(AdHocSpatialKey)
            || HandlerForbadeStorage(context.Response))
        {
            return;
        }

        var options = CurrentOptions(context);
        context.Response.Headers.CacheControl = FormatCacheControl(context, cacheClass, options);
        VaryHeader.AppendMissing(context.Response.Headers, VaryFor(cacheClass));
        if (options.CacheTagsEnabled && CdnCacheTags.ForResponse(Subject(context), context.Request.Path, context.Request.RouteValues["serviceId"]?.ToString())
                is { Length: > 0 } tags)
        {
            var tileOptions = context.RequestServices.GetService<IOptions<TileOptions>>()?.Value ?? new TileOptions();
            context.Response.Headers[options.CacheTagHeader!] = string.Join(',', tags.Concat(CdnTileRegionTags.ForNativeTile(context.Request.Path, tileOptions)));
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

        return TimeSpan.FromSeconds(Math.Max(0, ResolveLifetime(context, cacheClass, CurrentOptions(context)).Lifetime.SharedMaxAgeSeconds));
    }

    internal static ResponseCacheClass? DeclaredClass(HttpContext context)
        => context.Items.TryGetValue(ClassKey, out var declared) && declared is ResponseCacheClass cacheClass
            ? cacheClass
            : context.GetEndpoint()?.Metadata.GetMetadata<SharedCacheClassMetadata>()?.CacheClass;

    // The ETag service's own default is private, must-revalidate: that is a fallback, not a refusal.
    private static bool HandlerForbadeStorage(HttpResponse response)
        => response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase);

    // A metadata document may list several resources and filters out what the caller cannot read, so it needs no single grant.
    private static bool AccessPermitsSharing(HttpContext context, ResponseCacheClass cacheClass)
        => cacheClass is ResponseCacheClass.MetadataDocument
            ? !ResponseAccessVerdict.Restricted(context)
            : ResponseAccessVerdict.GrantedToAnonymous(context);

    private static readonly string[] TileVaryHeaders = [HeaderNames.Origin, HeaderNames.Accept, HeaderNames.AcceptEncoding];
    private static readonly string[] FeatureQueryVaryHeaders = [HeaderNames.Origin, HeaderNames.Accept, HeaderNames.AcceptEncoding, "Prefer"];
    private static readonly string[] MetadataDocumentVaryHeaders = [HeaderNames.Origin, HeaderNames.Accept, HeaderNames.AcceptEncoding, HeaderNames.AcceptLanguage];
    private static readonly string[] DefaultVaryHeaders = [HeaderNames.Origin, HeaderNames.AcceptEncoding];

    // s-maxage is always written: the CDN's own minimum is 0, so the origin header is the only lifetime it has.
    private static string FormatCacheControl(HttpContext context, ResponseCacheClass cacheClass, SharedCacheOptions options)
    {
        var (lifetime, purgeBacked) = ResolveLifetime(context, cacheClass, options);
        if (purgeBacked)
        {
            context.Items[PurgeBackedSentKey] = true;
        }

        Span<char> buffer = stackalloc char[160];
        var pos = 0;
        pos += AppendLiteral(buffer[pos..], "public, max-age=");
        pos += AppendNonNegativeSeconds(buffer[pos..], lifetime.MaxAgeSeconds);
        pos += AppendLiteral(buffer[pos..], ", s-maxage=");
        pos += AppendNonNegativeSeconds(buffer[pos..], lifetime.SharedMaxAgeSeconds);

        if (lifetime.StaleWhileRevalidateSeconds > 0)
        {
            pos += AppendLiteral(buffer[pos..], ", stale-while-revalidate=");
            pos += AppendNonNegativeSeconds(buffer[pos..], lifetime.StaleWhileRevalidateSeconds);
        }

        if (cacheClass is (ResponseCacheClass.Tile or ResponseCacheClass.FeatureQuery) && options.StaleIfErrorSeconds > 0)
        {
            pos += AppendLiteral(buffer[pos..], ", stale-if-error=");
            pos += AppendNonNegativeSeconds(buffer[pos..], options.StaleIfErrorSeconds);
        }

        return new string(buffer[..pos]);
    }

    private static int AppendLiteral(Span<char> destination, ReadOnlySpan<char> literal)
    {
        literal.CopyTo(destination);
        return literal.Length;
    }

    private static int AppendNonNegativeSeconds(Span<char> destination, int seconds)
    {
        if (!Math.Max(0, seconds).TryFormat(destination, out var written, provider: CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Cache-Control formatting buffer exhausted.");
        }

        return written;
    }

    private static SharedCacheOptions CurrentOptions(HttpContext context)
        => context.RequestServices?.GetService<IOptionsMonitor<SharedCacheOptions>>()?.CurrentValue ?? ProductDefaults;

    // An operator's emergency, per-layer or per-tileset lifetime wins over the purge-backed one.
    private static ResolvedLifetime ResolveLifetime(HttpContext context, ResponseCacheClass cacheClass, SharedCacheOptions options)
    {
        var subject = Subject(context);
        if (options.Emergency.AppliesTo(subject))
        {
            return new(SharedCacheEmergency.Lifetime, PurgeBacked: false);
        }

        if (subject?.LayerKey is { } layerKey && options.Layers.TryGetValue(layerKey, out var configuredForLayer))
        {
            return new(configuredForLayer, PurgeBacked: false);
        }

        if (context.Items.TryGetValue(LifetimeKey, out var declared) && declared is SharedCacheLifetime layerLifetime)
        {
            return new(layerLifetime, PurgeBacked: false);
        }

        return PurgeBackedLifetime(context, cacheClass, options) is { } purgeBackedLifetime
            ? new(purgeBackedLifetime, PurgeBacked: true)
            : new(options.DefaultFor(cacheClass), PurgeBacked: false);
    }

    private readonly record struct ResolvedLifetime(SharedCacheLifetime Lifetime, bool PurgeBacked);

    private static SharedCacheLifetime? PurgeBackedLifetime(HttpContext context, ResponseCacheClass cacheClass, SharedCacheOptions options)
        => options.PurgeBacked.LifetimeFor(cacheClass) is { } lifetime
           && options.PurgeBacked.AppliesTo(Subject(context))
           && !context.Items.ContainsKey(ProcessCacheKey)
           && PurgesReachCdn(context)
            ? lifetime
            : null;

    // Read once per request, so the lifetime written and the storage decision made later agree.
    private static bool PurgesReachCdn(HttpContext context)
    {
        if (!context.Items.TryGetValue(PurgesReachCdnKey, out var reaches))
        {
            reaches = context.RequestServices?.GetService<CdnPurgeReliability>()?.PurgesReachCdn() == true;
            context.Items[PurgesReachCdnKey] = reaches;
        }

        return reaches is true;
    }

    // Origin is always listed: the CORS policy reflects the request origin, and only on requests that send one.
    // A tile without an explicit format is PNG or vector by Accept.
    private static string[] VaryFor(ResponseCacheClass cacheClass) => cacheClass switch
    {
        ResponseCacheClass.Tile => TileVaryHeaders,
        ResponseCacheClass.FeatureQuery => FeatureQueryVaryHeaders,
        ResponseCacheClass.MetadataDocument => MetadataDocumentVaryHeaders,
        _ => DefaultVaryHeaders,
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
        string? serviceId = null;
        foreach (var name in ServiceRouteValues)
        {
            if (routeValues.TryGetValue(name, out var raw) && raw?.ToString() is { } value)
            {
                serviceId = value;
                break;
            }
        }

        return serviceId is null
            ? null
            : new SharedCacheSubject(
                serviceId,
                routeValues.TryGetValue("layerId", out var layerRaw) ? layerRaw?.ToString() : null,
                routeValues.TryGetValue("styleId", out var styleRaw) ? styleRaw?.ToString() : null);
    }
}

internal readonly record struct TilesetAddress(string ServiceId, string LayerId, string TileMatrixSetId)
{
    internal string TilesetKey => TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId);
}
