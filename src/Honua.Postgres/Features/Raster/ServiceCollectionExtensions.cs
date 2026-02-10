// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
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
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPostgresRasterStore(this IServiceCollection services)
    {
        // Register the main raster store implementation
        services.AddScoped<PostgresRasterStore>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<PostgresRasterStore>>();
            return new PostgresRasterStore(logger);
        });

        // Register the interface
        services.AddScoped<IRasterStore>(provider => provider.GetRequiredService<PostgresRasterStore>());

        // Register the map renderer implementation
        services.AddScoped<PostgresRasterMapRenderer>();
        services.AddScoped<IRasterMapRenderer>(provider => provider.GetRequiredService<PostgresRasterMapRenderer>());

        return services;
    }
}
