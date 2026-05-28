// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.MySql.Features.FeatureStore;
using Honua.MySql.Features.FeatureStore.Services;
using Honua.MySql.Features.HealthCheck;
using Honua.MySql.Features.Infrastructure;
using Honua.MySql.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Honua.MySql;

/// <summary>
/// Dependency-injection wiring for the MySQL/MariaDB read-only feature provider.
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MySQL/MariaDB provider services. Bound from the <c>MySql</c> configuration section.
    /// </summary>
    public static IServiceCollection AddMySqlServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new MySqlOptions();
        configuration.GetSection("MySql").Bind(options);

        MySqlOptionsValidator.ThrowIfInvalid(options);

        // Engine flavor is validated above; parse once so SQL builders can pre-bind it.
        var engineFlavor = Enum.Parse<MySqlEngineFlavor>(options.EngineFlavor, ignoreCase: true);

        // Strip unsupported capability tokens at startup so the catalog cannot advertise them.
        foreach (var svc in options.Services)
        {
            svc.Capabilities = svc.Capabilities
                .Where(c => !c.Equals("Create", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Update", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Delete", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Extract", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var mappings = BuildLayerMappings(options);

        // Pooled MySqlDataSource — singleton — provides connection pooling.
        services.AddSingleton(_ => new MySqlDataSourceBuilder(options.ConnectionString).Build());
        services.AddSingleton(_ => new MySqlLayerMappingRegistry(mappings));
        // Register the engine flavor enum boxed inside an immutable holder; AddSingleton<T>(T)
        // requires a reference type, and a tiny holder is cheaper than reading IOptions<MySqlOptions>
        // on every scoped activation.
        services.AddSingleton(new MySqlEngineFlavorHolder(engineFlavor));

        services.AddScoped<IDatabaseConnectionProvider>(sp =>
            new MySqlConnectionProvider(sp.GetRequiredService<MySqlDataSource>()));

        services.AddScoped<IFeatureQueryBuilder>(sp =>
            new MySqlFeatureQueryBuilder(
                sp.GetRequiredService<MySqlLayerMappingRegistry>(),
                sp.GetRequiredService<MySqlEngineFlavorHolder>().Flavor));

        services.AddScoped<IFeatureDataAccess>(sp =>
            new MySqlFeatureDataAccess(
                sp.GetRequiredService<IDatabaseConnectionProvider>(),
                sp.GetRequiredService<MySqlLayerMappingRegistry>(),
                sp.GetService<Core.Features.Infrastructure.Monitoring.IPerformanceMonitor>(),
                sp.GetRequiredService<ILogger<MySqlFeatureDataAccess>>(),
                sp.GetRequiredService<MySqlEngineFlavorHolder>().Flavor));

        services.AddScoped<IFeatureCacheManager>(sp =>
            new MySqlFeatureCacheManager(sp.GetRequiredService<MySqlLayerMappingRegistry>()));

        // ReadinessCheckService requires IDatabaseHealthChecker; without this registration
        // resolving /ready under DataSource:Provider=mysql would fail DI activation. The
        // checker drives a SELECT 1 through the same pooled MySqlDataSource the store uses.
        services.AddScoped<IDatabaseHealthChecker, MySqlDatabaseHealthChecker>();

        services.AddScoped<MySqlFeatureStore>();
        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<MySqlFeatureStore>());
        services.AddScoped<IFeatureReader>(sp => sp.GetRequiredService<MySqlFeatureStore>());
        services.AddScoped<IPagedFeatureReader>(sp => sp.GetRequiredService<MySqlFeatureStore>());
        services.AddScoped<IStreamingFeatureStore>(sp => sp.GetRequiredService<MySqlFeatureStore>());

        // Mirror the DuckDB read-only surface so DI consumers that require these segregated
        // capabilities (FeatureServer query executor, gRPC service, OGC handlers, WFS, OData)
        // can activate under DataSource:Provider=mysql. The slice is read/query-only, so the
        // write-shaped surfaces are no-op or NotSupportedException stubs.
        services.AddScoped<IFeatureWriter>(_ => new ReadOnlyFeatureWriter("MySQL/MariaDB"));
        services.AddScoped<IReplicaRepository>(_ => new NoOpReplicaRepository());
        services.AddScoped<IChangeTracker>(_ => new NoOpChangeTracker());
        services.AddScoped<ITileProvider>(_ => new ReadOnlyTileProvider("MySQL/MariaDB"));
        services.AddScoped<IGmlFeatureStore>(_ => new ReadOnlyGmlFeatureStore("MySQL/MariaDB"));

        services.AddScoped<ISqlFilterTranslator>(sp =>
            new MySqlSqlFilterTranslator(sp.GetRequiredService<MySqlEngineFlavorHolder>().Flavor));

        return services;
    }

    private static List<MySqlLayerMapping> BuildLayerMappings(MySqlOptions options)
    {
        var mappings = new List<MySqlLayerMapping>(options.Layers.Length);
        foreach (var layer in options.Layers)
        {
            // Validate identifiers eagerly so any backtick-injection vector is rejected at startup.
            MySqlIdentifier.ValidateFieldName(layer.PrimaryKeyColumn);
            MySqlIdentifier.ValidateFieldName(layer.GeometryColumn);
            foreach (var attr in layer.Attributes)
            {
                MySqlIdentifier.ValidateFieldName(attr);
            }

            // GeometryType is pre-validated by MySqlOptionsValidator; Parse (not TryParse)
            // surfaces any future regression that lets an unknown value through instead
            // of silently defaulting to Point.
            var geometryType = Enum.Parse<GeometryType>(layer.GeometryType, ignoreCase: true);

            mappings.Add(new MySqlLayerMapping
            {
                LayerId = layer.Id,
                TableName = layer.Table,
                SchemaName = layer.Schema,
                GeometryColumn = layer.GeometryColumn,
                PrimaryKeyColumn = layer.PrimaryKeyColumn,
                Srid = layer.Srid,
                AttributeColumns = layer.Attributes,
                AttributeColumnTypes = layer.AttributeTypes,
                GeometryType = geometryType
            });
        }

        return mappings;
    }

}
