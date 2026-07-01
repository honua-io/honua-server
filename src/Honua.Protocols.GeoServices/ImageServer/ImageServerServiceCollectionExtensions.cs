// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Service collection extensions for Image Server feature registration.
/// </summary>
internal static class ImageServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers Image Server services with dependency injection.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">
    /// Application configuration used to bind <see cref="ImageServerTileMetadataOptions"/>
    /// (the opt-in tiled-consumption metadata flag, #1648).
    /// </param>
    public static IServiceCollection AddImageServer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Opt-in WebMercatorQuad tileInfo advertising. Defaults OFF to keep the
        // dynamic (#1456-safe) ImageServer contract the ArcGIS Maps SDK for .NET
        // native runtime requires; deployments serving tiled Esri clients enable it
        // via GeoServices:ImageServer:TileMetadata:Enabled (#1648).
        services.Configure<ImageServerTileMetadataOptions>(
            configuration.GetSection(ImageServerTileMetadataOptions.SectionName));

        // Register handlers
        services.AddScoped<ImageServerMetadataHandler>();
        services.AddScoped<ImageServerMultidimensionalInfoHandler>();
        services.AddScoped<ImageServerSlicesHandler>();
        services.AddScoped<ImageServerExportHandler>();
        services.AddScoped<ImageServerIdentifyHandler>();
        services.AddScoped<ImageServerTileHandler>();
        services.AddScoped<ImageServerCatalogQueryHandler>();
        services.AddScoped<ImageServerStatisticsHistogramsHandler>();
        services.AddScoped<ImageServerSamplesHandler>();
        services.AddScoped<ImageServerKeyPropertiesHandler>();
        services.AddScoped<ImageServerLegendHandler>();
        services.AddScoped<ImageServerAnalyzeHandler>();
        services.AddScoped<ImageServerRasterMetadataHandler>();
        services.AddScoped<ImageServerCoordinateMetadataHandler>();
        services.AddScoped<ImageServerProjectHandler>();
        services.AddScoped<ImageServerExportTilesHandler>();
        services.AddScoped<ImageServerFindHandler>();
        services.AddScoped<ImageServerMeasureHandler>();
        services.AddScoped<ImageServerWmtsHandler>();

        // Register supporting services
        services.TryAddScoped<SpatialReferenceResolver>();

        // CRS/datum/transform seam for the project operation: folds the spatial-reference
        // resolver, datum-transformation catalog, and optional coordinate transform service
        // behind one dependency so ImageServerProjectHandler stays within the collaborator limit.
        services.TryAddScoped<ImageServerCoordinateProjection>();

        // Shared Esri datum-transformation catalog (WKID -> PROJ pipeline) used by the
        // project operation. TryAdd keeps a single instance shared with the FeatureServer
        // registration regardless of protocol registration order.
        services.TryAddSingleton<IDatumTransformationCatalog>(static _ => EsriDatumTransformationCatalog.Create());
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

        // Per-slice point sampler for getSamples multidimensionalDefinition (#1869).
        // Reuses the shared Zarr catalog + subset reader; ICloudRangeReader instances
        // are contributed by the active cloud-storage providers.
        services.AddScoped<ZarrPointSampler>();

        return services;
    }
}
