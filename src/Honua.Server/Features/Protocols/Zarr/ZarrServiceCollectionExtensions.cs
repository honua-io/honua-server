// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Server.Features.Protocols.Zarr;

/// <summary>
/// Service collection extensions for the Zarr coverage feature.
/// </summary>
internal static class ZarrServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AOT-safe Zarr metadata/subset readers and the in-memory catalog.
    /// </summary>
    public static IServiceCollection AddZarrServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IZarrMetadataReader, ZarrMetadataExtractor>();
        services.AddSingleton<IZarrSubsetReader, ZarrSubsetReader>();
        services.AddSingleton<IZarrStore, InMemoryZarrStore>();
        return services;
    }
}
