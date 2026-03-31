// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.StaticMap;

/// <summary>
/// Service collection extensions for StaticMap feature registration.
/// </summary>
internal static class StaticMapServiceCollectionExtensions
{
    /// <summary>
    /// Registers StaticMap services. StaticMap reuses MapServer rendering infrastructure
    /// (SkiaSharp, StyleTranslator, WkbToSkiaConverter) and core services (IFeatureReader,
    /// ILayerStyleCatalog) which are already registered.
    /// </summary>
    public static IServiceCollection AddStaticMap(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // StaticMap uses core services and MapServer rendering utilities.
        // No additional registrations needed.

        return services;
    }
}
