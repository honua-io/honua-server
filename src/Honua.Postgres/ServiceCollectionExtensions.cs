// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Abstractions;
using Honua.Core.HealthCheck;
using Honua.Postgres.Features;
using Honua.Postgres.HealthCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Postgres;

/// <summary>
/// Dependency injection extensions for PostgreSQL services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add PostgreSQL services including feature store and health checking
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration to get connection string from</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddPostgreSqlServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register NpgsqlDataSource as specified in Issue #3
        services.AddSingleton<NpgsqlDataSource>(serviceProvider =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("DefaultConnection connection string is required for PostgreSQL services");
            }

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

            // Note: Not using EnableDynamicJson() for AOT compatibility
            // Manual JSON serialization is used instead for JSONB parameters

            return dataSourceBuilder.Build();
        });

        // Register feature store implementation
        services.AddScoped<IFeatureStore, PostgresFeatureStore>();

        // Register health checker
        services.AddScoped<IDatabaseHealthChecker, PostgresDatabaseHealthChecker>();

        return services;
    }
}
