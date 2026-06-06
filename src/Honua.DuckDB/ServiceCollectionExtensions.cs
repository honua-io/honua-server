// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.DuckDB.Features.FeatureStore;
using Honua.DuckDB.Features.FeatureStore.Services;
using DuckDB.NET.Data;
using Honua.DuckDB.Features.Infrastructure;
using Honua.DuckDB.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB;

/// <summary>
/// Dependency injection extensions for DuckDB services
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all DuckDB provider services including feature store, catalog, and connections.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration for DuckDB options</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddDuckDBServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new DuckDBOptions();
        configuration.GetSection("DuckDB").Bind(options);

        DuckDBOptionsValidator.ThrowIfInvalid(options);

        // Validate and strip unsupported capabilities at startup. The DuckDB provider is
        // read-only in V1, so editing (Create/Update/Delete) and replica extract workflows
        // are removed before they reach the catalog. Extract is dropped because there is
        // no DuckDB-side replica persistence path — see ReadOnlyReplicaRepository.
        foreach (var svc in options.Services)
        {
            var originalCaps = svc.Capabilities;
            svc.Capabilities = originalCaps
                .Where(c => !c.Equals("Create", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Update", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Delete", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Extract", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        // Build connection string
        var connectionString = options.ReadOnly
            ? $"Data Source={options.DatabasePath};Access Mode=ReadOnly"
            : $"Data Source={options.DatabasePath}";

        services.AddSingleton<ISqlDialect>(DuckDbSqlDialect.Instance);

        // Register infrastructure singletons
        services.AddSingleton<DuckDBSpatialBootstrap>(sp =>
            new DuckDBSpatialBootstrap(
                options.SpatialExtensionPath,
                DuckDBExternalSourceSql.GetRequiredExtensions(options),
                DuckDBExternalSourceSql.BuildConnectionSettingCommands(options),
                DuckDBExternalSourceSql.BuildExternalSourcePlans(options.Layers),
                sp.GetRequiredService<ILogger<DuckDBSpatialBootstrap>>()));

        services.AddSingleton<DuckDBLayerRegistry>(sp =>
        {
            var mappings = BuildLayerMappings(options, connectionString, sp);
            return new DuckDBLayerRegistry(mappings);
        });

        // Register connection provider (scoped — new connection per request)
        services.AddScoped<IDatabaseConnectionProvider>(sp =>
            new DuckDBConnectionProvider(
                connectionString,
                sp.GetRequiredService<DuckDBSpatialBootstrap>(),
                sp.GetRequiredService<ILogger<DuckDBConnectionProvider>>()));

        // Audit-C3 session abstraction registered alongside the legacy provider
        // during the progressive migration (see ADR 0046).
        services.AddScoped<IDatabaseSessionFactory>(sp =>
            new Features.Infrastructure.Session.DuckDbDatabaseSessionFactory(
                sp.GetRequiredService<IDatabaseConnectionProvider>()));

        // Register query builder (scoped — depends on layer registry)
        services.AddScoped<IFeatureQueryBuilder>(sp =>
            new DuckDBFeatureQueryBuilder(
                sp.GetRequiredService<DuckDBLayerRegistry>()));

        // Register data access (scoped)
        services.AddScoped<IFeatureDataAccess>(sp =>
            new DuckDBFeatureDataAccess(
                sp.GetRequiredService<IDatabaseConnectionProvider>(),
                sp.GetRequiredService<DuckDBLayerRegistry>(),
                sp.GetService<Core.Features.Infrastructure.Monitoring.IPerformanceMonitor>(),
                sp.GetRequiredService<ILogger<DuckDBFeatureDataAccess>>()));

        // Register cache manager (scoped)
        services.AddScoped<IFeatureCacheManager>(sp =>
            new DuckDBFeatureCacheManager(
                sp.GetRequiredService<DuckDBLayerRegistry>()));

        // Register the main feature store composition
        services.AddScoped<DuckDBFeatureStore>();

        // Register segregated interfaces
        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<IFeatureReader>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<IFeatureWriter>(_ => new ReadOnlyFeatureWriter("DuckDB"));
        services.AddScoped<IReplicaRepository>(_ => new NoOpReplicaRepository());
        services.AddScoped<IReplicaConflictRepository>(_ => new NoOpReplicaConflictRepository());
        services.AddScoped<IChangeTracker>(_ => new NoOpChangeTracker());
        services.AddScoped<IVersionManager>(_ => new NoOpVersionManager());
        services.AddScoped<IGeoJsonFeatureStore>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<IStreamingFeatureStore>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<ITileProvider>(_ => new ReadOnlyTileProvider("DuckDB"));
        services.AddScoped<IGmlFeatureStore>(_ => new ReadOnlyGmlFeatureStore("DuckDB"));

        // Capability provider for the feature-change transactional outbox (#692). DuckDB is
        // read-only so it reports SupportsTransactionalOutbox = false. The dispatcher will
        // skip startup; mutation event delivery falls through to the post-commit publish +
        // retry queue path. Registered via TryAdd so the Postgres provider wins when both
        // backends are configured.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.DuckDB.Features.Infrastructure.Events.Outbox.DuckDbOutboxCapabilityProvider>();

        return services;
    }

    private static List<DuckDBLayerMapping> BuildLayerMappings(
        DuckDBOptions options, string connectionString, IServiceProvider sp)
    {
        var mappings = new List<DuckDBLayerMapping>(options.Layers.Length);
        var logger = sp.GetRequiredService<ILogger<DuckDBLayerRegistry>>();

        foreach (var layerOpt in options.Layers)
        {
            var (attributeColumns, attributeColumnTypes) = layerOpt.Attributes is { Length: > 0 }
                ? (layerOpt.Attributes.ToList(), new Dictionary<string, string>())
                : DiscoverAttributeColumns(connectionString, options, layerOpt, logger);

            DuckDbLog.LayerRegistered(
                logger,
                layerOpt.Id,
                layerOpt.Table,
                layerOpt.GeometryColumn,
                layerOpt.ObjectIdColumn,
                attributeColumns.Count);

            mappings.Add(new DuckDBLayerMapping
            {
                LayerId = layerOpt.Id,
                TableName = layerOpt.Table,
                GeometryColumn = layerOpt.GeometryColumn,
                ObjectIdColumn = layerOpt.ObjectIdColumn,
                Srid = layerOpt.Srid,
                AttributeColumns = attributeColumns,
                AttributeColumnTypes = attributeColumnTypes
            });
        }

        return mappings;
    }

    private static (List<string> Names, Dictionary<string, string> Types) DiscoverAttributeColumns(
        string connectionString, DuckDBOptions options, DuckDBLayerOptions layerOpt, ILogger logger)
    {
        try
        {
            using var connection = new DuckDBConnection(connectionString);
            connection.Open();
            PrepareSchemaDiscoveryConnection(connection, options, layerOpt);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({DuckDBExternalSourceSql.QuoteLiteral(layerOpt.Table)})";
            using var reader = cmd.ExecuteReader();

            var columns = new List<string>();
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var columnName = reader.GetString(1);
                if (!string.Equals(columnName, layerOpt.GeometryColumn, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(columnName, layerOpt.ObjectIdColumn, StringComparison.OrdinalIgnoreCase))
                {
                    columns.Add(columnName);
                    types[columnName] = reader.GetString(2);
                }
            }

            return (columns, types);
        }
        catch (Exception ex)
        {
            DuckDbLog.AttributeDiscoveryFailed(logger, layerOpt.Id, layerOpt.Table, ex);
            return ([], new Dictionary<string, string>());
        }
    }

    private static void PrepareSchemaDiscoveryConnection(
        DuckDBConnection connection,
        DuckDBOptions options,
        DuckDBLayerOptions layerOpt)
    {
        if (layerOpt.ExternalSource is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.SpatialExtensionPath))
        {
            ExecuteNonQuery(
                connection,
                $"SET extension_directory={DuckDBExternalSourceSql.QuoteLiteral(options.SpatialExtensionPath)}");
        }

        foreach (var extension in DuckDBExternalSourceSql.GetRequiredExtensions(options))
        {
            ExecuteNonQuery(connection, $"INSTALL {extension}");
            ExecuteNonQuery(connection, $"LOAD {extension}");
        }

        ExecuteNonQuery(connection, "INSTALL spatial");
        ExecuteNonQuery(connection, "LOAD spatial");

        foreach (var command in DuckDBExternalSourceSql.BuildConnectionSettingCommands(options))
        {
            ExecuteNonQuery(connection, command.Sql);
        }

        ExecuteNonQuery(connection, DuckDBExternalSourceSql.BuildCreateTempViewSql(layerOpt));
    }

    private static void ExecuteNonQuery(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

}
