// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Services;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Service collection extensions for Image Server feature registration.
/// </summary>
internal static class ImageServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers Image Server services with dependency injection.
    /// </summary>
    public static IServiceCollection AddImageServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register handlers
        services.AddScoped<ImageServerMetadataHandler>();
        services.AddScoped<ImageServerMultidimensionalInfoHandler>();
        services.AddScoped<ImageServerExportHandler>();
        services.AddScoped<ImageServerIdentifyHandler>();
        services.AddScoped<ImageServerTileHandler>();
        services.AddScoped<ImageServerCatalogQueryHandler>();
        services.AddScoped<ImageServerStatisticsHistogramsHandler>();
        services.AddScoped<ImageServerLegendHandler>();
        services.AddScoped<ImageServerAnalyzeHandler>();

        // Register supporting services
        services.AddScoped<IImageServerLayerResolver, MetadataV2ImageServerLayerResolver>();
        services.AddScoped<IImageServerCatalogReader, ImageServerCatalogReader>();
        services.AddSingleton<IImageServerCatalogFilterEvaluator, ImageServerCatalogFilterEvaluator>();
        services.AddSingleton<IImageServerLegendSwatchBuilder, ImageServerLegendSwatchBuilder>();
        services.AddSingleton<IImageServerRasterFunctionPlanner, ImageServerRasterFunctionPlanner>();

        // Multidimensional coverage info builder. IMultidimensionalCoverageStore is
        // registered by the active data provider and is optional here: deployments
        // without a multidimensional coverage store resolve a null store and always
        // report "not multidimensional".
        services.AddScoped<IImageServerMultidimensionalInfoBuilder>(static provider =>
            new ImageServerMultidimensionalInfoBuilder(
                provider.GetService<Core.Features.Raster.Multidimensional.Abstractions.IMultidimensionalCoverageStore>()));

        return services;
    }
}
