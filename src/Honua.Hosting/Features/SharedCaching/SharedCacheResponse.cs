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
    private static readonly object LifetimeKey = new();
    private static readonly object GenerationAddressedKey = new();
    private static readonly object LayerKeyKey = new();

    internal static void UseLifetime(HttpContext context, SharedCacheLifetime lifetime)
        => context.Items[LifetimeKey] = lifetime;

    internal static void UseTileset(HttpContext context, TileOptions tileOptions, TilesetAddress tileset)
    {
        UseLayer(context, tileset.LayerKey);
        if (TilesetTtlResolver.ResolveTilesetTtl(tileOptions, tileset.TilesetKey) is { } tilesetTtl)
        {
            UseLifetime(context, SharedCacheLifetime.ForBrowserAndCdn(tilesetTtl));
        }
    }

    // The key an operator names in SharedCache:Emergency:Layers, "{serviceId}/{layerId}".
    internal static void UseLayer(HttpContext context, string layerKey)
        => context.Items[LayerKeyKey] = layerKey;

    internal static void MarkGenerationAddressed(HttpContext context)
        => context.Items[GenerationAddressedKey] = true;

    internal static bool IsGenerationAddressed(HttpContext context)
        => context.Items.ContainsKey(GenerationAddressedKey);

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

        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<SharedCacheOptions>>().CurrentValue;
        context.Response.Headers.CacheControl = FormatCacheControl(context, cacheClass, options);
        AddMissingVary(context.Response, VaryFor(cacheClass));
    }

    // The ETag service's own default is private, must-revalidate: that is a fallback, not a refusal.
    private static bool HandlerForbadeStorage(HttpResponse response)
        => response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase);

    internal static ResponseCacheClass? DeclaredClass(HttpContext context)
        => context.GetEndpoint()?.Metadata.GetMetadata<SharedCacheClassMetadata>()?.CacheClass;

    // A metadata document may list several resources and filters out what the caller cannot read, so it needs no single grant.
    private static bool AccessPermitsSharing(HttpContext context, ResponseCacheClass cacheClass)
        => cacheClass is ResponseCacheClass.MetadataDocument
            ? !ResponseAccessVerdict.Restricted(context)
            : ResponseAccessVerdict.GrantedToAnonymous(context);

    private static string FormatCacheControl(HttpContext context, ResponseCacheClass cacheClass, SharedCacheOptions options)
    {
        var emergency = options.Emergency.AppliesTo(LayerKey(context));
        var lifetime = ResolveLifetime(context, cacheClass, options, emergency);
        var directives = new List<string>
        {
            "public",
            $"max-age={Seconds(lifetime.MaxAgeSeconds)}",
            $"s-maxage={Seconds(lifetime.SharedMaxAgeSeconds)}",
        };

        if (IsGenerationAddressed(context) && !emergency)
        {
            directives.Add("immutable");
        }
        else if (lifetime.StaleWhileRevalidateSeconds > 0)
        {
            directives.Add($"stale-while-revalidate={Seconds(lifetime.StaleWhileRevalidateSeconds)}");
        }

        if (cacheClass is (ResponseCacheClass.Tile or ResponseCacheClass.FeatureQuery) && options.StaleIfErrorSeconds > 0)
        {
            directives.Add($"stale-if-error={Seconds(options.StaleIfErrorSeconds)}");
        }

        return string.Join(", ", directives);
    }

    private static SharedCacheLifetime ResolveLifetime(
        HttpContext context, ResponseCacheClass cacheClass, SharedCacheOptions options, bool emergency)
    {
        if (emergency)
        {
            return SharedCacheEmergency.Lifetime;
        }

        if (IsGenerationAddressed(context))
        {
            return SharedCacheLifetime.ForBrowserAndCdn(options.GenerationAddressedLifetimeSeconds);
        }

        return context.Items.TryGetValue(LifetimeKey, out var declared) && declared is SharedCacheLifetime layerLifetime
            ? layerLifetime
            : options.DefaultFor(cacheClass);
    }

    private static string? LayerKey(HttpContext context)
        => context.Items.TryGetValue(LayerKeyKey, out var layerKey) ? layerKey as string : null;

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

internal readonly record struct TilesetAddress(string ServiceId, string LayerId, string TileMatrixSetId)
{
    internal string LayerKey => $"{ServiceId}/{LayerId}";

    internal string TilesetKey => TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId);
}
