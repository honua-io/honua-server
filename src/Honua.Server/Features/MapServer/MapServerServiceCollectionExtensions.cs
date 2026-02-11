// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.MapServer;

/// <summary>
/// Service collection extensions for MapServer feature registration.
/// </summary>
internal static class MapServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers MapServer services. MapServer adds SkiaSharp-based rendering for
    /// export, identify, and legend operations. Query uses FeatureServer handlers.
    /// </summary>
    public static IServiceCollection AddMapServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // MapServer uses core services (IFeatureReader, ILayerStyleCatalog) which are already registered.
        // Rendering is handled by static utility classes (SkiaMapRenderer, StyleTranslator, etc.).
        // Query endpoints reuse FeatureServer query handling for ArcGIS REST parity.

        return services;
    }
}
