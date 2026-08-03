// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Service collection extensions for registering PostgreSQL raster store services.
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the isolated PostGIS raster worker pool and governance boundary. This method is
    /// intentionally separate from <see cref="AddPostgresRasterStore"/> and must only be called by
    /// the dedicated <c>raster-postgis</c> worker composition.
    /// </summary>
    public static IServiceCollection AddPostgisRasterExecutionGovernance(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PostgisRasterExecutionOptions>()
            .Bind(configuration.GetSection(PostgisRasterExecutionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PostgisRasterExecutionOptions>,
                PostgisRasterExecutionOptionsValidator>());
        services.TryAddSingleton(serviceProvider =>
        {
            var connectionString = configuration.GetConnectionString(
                PostgisRasterExecutionOptions.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{PostgisRasterExecutionOptions.ConnectionStringName} is required " +
                    "for the dedicated PostGIS raster worker.");
            }

            return PostgisRasterDataSource.Create(
                connectionString,
                serviceProvider.GetRequiredService<IOptions<PostgisRasterExecutionOptions>>().Value);
        });
        services.TryAddSingleton<PostgisRasterAdmissionController>();
        services.TryAddSingleton<IPostgisRasterExecutionSessionFactory>(serviceProvider =>
            new PostgisRasterExecutionSessionFactory(
                serviceProvider.GetRequiredService<PostgisRasterDataSource>(),
                serviceProvider.GetRequiredService<PostgisRasterAdmissionController>(),
                serviceProvider.GetRequiredService<IOptions<PostgisRasterExecutionOptions>>(),
                serviceProvider.GetService<ITenantSchemaResolver>()));
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL raster store services with dependency injection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="schemaName">Optional database schema name (defaults to "honua")</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPostgresRasterStore(this IServiceCollection services, string? schemaName = null)
    {
        // Register the main raster store implementation
        services.AddScoped<IRasterStore>(provider =>
            new PostgresRasterStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresRasterStore>>(),
                schemaName));

        // Register the map renderer implementation
        services.AddScoped<IRasterMapRenderer>(provider =>
            new PostgresRasterMapRenderer(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresRasterMapRenderer>>(),
                schemaName));

        // Register raster import service
        services.AddScoped<IRasterImportService>(provider =>
            new PostgresRasterImportService(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ICrsDetectionService>(),
                provider.GetRequiredService<ILogger<PostgresRasterImportService>>(),
                schemaName));

        // Register COG catalog store
        services.AddScoped<ICogStore>(provider =>
            new PostgresCogStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresCogStore>>(),
                schemaName));

        // Register multidimensional coverage catalog store (cloud-optimized HDF5 / NetCDF4)
        services.AddScoped<IMultidimensionalCoverageStore>(provider =>
            new PostgresMultidimensionalCoverageStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresMultidimensionalCoverageStore>>(),
                schemaName));

        // Register surface-analysis service
        services.AddScoped<ISurfaceAnalysisService>(provider =>
            new PostgresSurfaceAnalysisService(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresSurfaceAnalysisService>>(),
                schemaName));

        services.AddScoped<ITerrainTileService>(provider =>
            new PostgresTerrainTileService(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ICrsRegistry>(),
                provider.GetRequiredService<IRasterStore>(),
                provider.GetRequiredService<ILogger<PostgresTerrainTileService>>(),
                schemaName));

        services.AddScoped<IElevationService>(provider =>
            new PostgresElevationService(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetRequiredService<ICrsRegistry>(),
                provider.GetRequiredService<IRasterStore>(),
                provider.GetRequiredService<ILogger<PostgresElevationService>>(),
                schemaName));

        return services;
    }
}
