// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Admin;
using Honua.Postgres.Features.Attachments;
using Honua.Postgres.Features.Catalog;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.HealthCheck;
using Honua.Postgres.Features.Import;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Postgres;

/// <summary>
/// Dependency injection extensions for PostgreSQL services
/// </summary>
internal static class ServiceCollectionExtensions
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

        // Register attachment store implementation
        services.AddScoped<IAttachmentStore, PostgresAttachmentStore>();

        // Register layer catalog implementation
        services.AddScoped<ILayerCatalog, PostgresLayerCatalog>();

        // Register table discovery implementation
        services.AddScoped<ITableDiscoveryService, PostgreSqlTableDiscoveryService>();

        // Register health checker
        services.AddScoped<IDatabaseHealthChecker, PostgresDatabaseHealthChecker>();

        // Register database connection provider with resilience policies
        services.AddScoped<IDatabaseConnectionProvider, PostgresDatabaseConnectionProvider>();

        // Register file import service with NetTopologySuite support
        services.AddScoped<IFileImportService>(serviceProvider =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("DefaultConnection connection string is required for file import services");
            }

            return new FileImportService(connectionString);
        });

        return services;
    }
}
