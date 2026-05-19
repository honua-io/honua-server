// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Service collection extensions for registering PostgreSQL raster store services.
/// </summary>
internal static class ServiceCollectionExtensions
{
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
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresRasterStore>>(),
                schemaName));

        // Register the map renderer implementation
        services.AddScoped<IRasterMapRenderer>(provider =>
            new PostgresRasterMapRenderer(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresRasterMapRenderer>>(),
                schemaName));

        // Register raster import service
        services.AddScoped<IRasterImportService>(provider =>
            new PostgresRasterImportService(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ICrsDetectionService>(),
                provider.GetRequiredService<ILogger<PostgresRasterImportService>>(),
                schemaName));

        // Register COG catalog store
        services.AddScoped<ICogStore>(provider =>
            new PostgresCogStore(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresCogStore>>(),
                schemaName));

        // Register multidimensional coverage catalog store (cloud-optimized HDF5 / NetCDF4)
        services.AddScoped<IMultidimensionalCoverageStore>(provider =>
            new PostgresMultidimensionalCoverageStore(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresMultidimensionalCoverageStore>>(),
                schemaName));

        // Register surface-analysis service
        services.AddScoped<ISurfaceAnalysisService>(provider =>
            new PostgresSurfaceAnalysisService(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ILogger<PostgresSurfaceAnalysisService>>(),
                schemaName));

        services.AddScoped<ITerrainTileService>(provider =>
            new PostgresTerrainTileService(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ICrsRegistry>(),
                provider.GetRequiredService<IRasterStore>(),
                provider.GetRequiredService<ILogger<PostgresTerrainTileService>>(),
                schemaName));

        services.AddScoped<IElevationService>(provider =>
            new PostgresElevationService(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<ICrsRegistry>(),
                provider.GetRequiredService<IRasterStore>(),
                provider.GetRequiredService<ILogger<PostgresElevationService>>(),
                schemaName));

        return services;
    }
}
