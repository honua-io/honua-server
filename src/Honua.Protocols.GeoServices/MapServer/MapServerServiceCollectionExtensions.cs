// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Protocols.GeoServices.MapServer;

/// <summary>
/// Service collection extensions for MapServer feature registration.
/// </summary>
internal static class MapServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers MapServer services. MapServer adds SkiaSharp-based rendering for
    /// export, identify, and legend operations. Query uses FeatureServer handlers.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">
    /// Application configuration used to bind <see cref="MapServerDynamicLayersOptions"/>
    /// (the opt-in <c>dynamicLayers</c> workspace allowlist, #1660).
    /// </param>
    public static IServiceCollection AddMapServer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // MapServer uses core services (IFeatureReader, ILayerStyleCatalog) which are already registered.
        // Rendering is handled by static utility classes (SkiaMapRenderer, StyleTranslator, etc.).
        // Query endpoints reuse FeatureServer query handling for ArcGIS REST parity.
        services.AddSingleton<Honua.Infrastructure.Rendering.RasterRenderCapacityLimiter>();

        // Opt-in dynamicLayers workspace (source.type=dataLayer) allowlist. Defaults OFF; the
        // mapLayer dynamic-layer path stays available regardless. Workspace data layers resolve
        // to published resources backed by the allowlisted workspace and never accept ad-hoc SQL
        // or unpublished tables (#1660).
        services.Configure<MapServerDynamicLayersOptions>(
            configuration.GetSection(MapServerDynamicLayersOptions.SectionName));

        // Durable tile-export map source: the singleton producer/fence resolve scoped services per
        // job via IServiceScopeFactory, so the singleton TileExport executor can render MapServer
        // tiles from a pinned plan HTTP-independently (#2706).
        services.AddSingleton<Honua.Infrastructure.Tiles.ITileExportPackageProducer, Tiles.MapTileExportProducer>();
        services.AddSingleton<Honua.Infrastructure.Tiles.ITileExportSourceFence, Tiles.MapTileExportSourceFence>();

        return services;
    }
}
