// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.MySql.Features.Catalog;
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
        services.AddScoped<IFeatureWriter>(_ => new ReadOnlyMySqlFeatureWriter());
        services.AddScoped<IReplicaRepository>(_ => new ReadOnlyMySqlReplicaRepository());
        services.AddScoped<IReplicaConflictStore>(_ => new ReadOnlyReplicaConflictStore());
        services.AddScoped<IChangeTracker>(_ => new ReadOnlyMySqlChangeTracker());
        services.AddScoped<ITileProvider>(_ => new ReadOnlyMySqlTileProvider());
        services.AddScoped<IGmlFeatureStore>(_ => new ReadOnlyMySqlGmlFeatureStore());

        services.AddScoped<ISqlFilterTranslator>(sp =>
            new MySqlSqlFilterTranslator(sp.GetRequiredService<MySqlEngineFlavorHolder>().Flavor));

        services.AddScoped<ILayerCatalog>(sp =>
        {
            var (layers, serviceDefs) = BuildCatalogEntries(options, mappings);
            return new MySqlLayerCatalog(layers, serviceDefs, sp.GetRequiredService<ILogger<MySqlLayerCatalog>>());
        });

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

    private static (List<LayerDefinition> Layers, List<ServiceDefinition> Services) BuildCatalogEntries(
        MySqlOptions options, IReadOnlyList<MySqlLayerMapping> mappings)
    {
        var mappingByLayerId = mappings.ToDictionary(m => m.LayerId);
        var layers = new List<LayerDefinition>(options.Layers.Length);
        var layerMap = new Dictionary<int, LayerDefinition>();

        foreach (var layerOpt in options.Layers)
        {
            var geometryType = Enum.Parse<GeometryType>(layerOpt.GeometryType, ignoreCase: true);

            var srs = SpatialReference.Create(layerOpt.Srid);

            var fields = new List<FieldDefinition>
            {
                new(layerOpt.PrimaryKeyColumn, FieldType.BigInteger, Nullable: false, Description: "Unique object identifier")
            };

            if (geometryType != GeometryType.None)
            {
                fields.Add(new(layerOpt.GeometryColumn, FieldType.Geometry, Nullable: false, Description: "Geometry field"));
            }

            if (mappingByLayerId.TryGetValue(layerOpt.Id, out var mapping))
            {
                foreach (var attr in mapping.AttributeColumns)
                {
                    var fieldType = mapping.AttributeColumnTypes.TryGetValue(attr, out var typeName)
                        ? MapMySqlType(typeName)
                        : FieldType.String;
                    fields.Add(new FieldDefinition(attr, fieldType));
                }
            }

            var layer = new LayerDefinition(
                layerOpt.Id,
                layerOpt.Name,
                layerOpt.Description,
                geometryType,
                srs,
                fields.ToArray(),
                SupportsAttachments: false,
                StorageMapping: new LayerStorageMapping(
                    layerOpt.Table,
                    SchemaName: layerOpt.Schema,
                    DatabaseName: layerOpt.Schema,
                    PrimaryKeyColumn: layerOpt.PrimaryKeyColumn,
                    GeometryColumn: geometryType == GeometryType.None ? null : layerOpt.GeometryColumn,
                    StorageSrid: layerOpt.Srid));

            layers.Add(layer);
            layerMap[layerOpt.Id] = layer;
        }

        var services = new List<ServiceDefinition>(options.Services.Length);
        foreach (var svcOpt in options.Services)
        {
            var svcLayers = svcOpt.LayerIds
                .Where(id => layerMap.ContainsKey(id))
                .Select(id => layerMap[id])
                .ToArray();

            if (svcLayers.Length == 0)
            {
                continue;
            }

            CatalogMetadata? metadata = null;
            if (svcOpt.EnabledProtocols is { Length: > 0 })
            {
                metadata = new CatalogMetadata { EnabledProtocols = svcOpt.EnabledProtocols };
            }

            services.Add(new ServiceDefinition(
                svcOpt.Name,
                svcOpt.Description ?? $"MySQL/MariaDB feature service: {svcOpt.Name}",
                svcLayers,
                svcLayers[0].SpatialReference,
                Capabilities: svcOpt.Capabilities,
                Metadata: metadata));
        }

        return (layers, services);
    }

    /// <summary>
    /// Maps a MySQL/MariaDB column type name (case-insensitive) to a <see cref="FieldType"/>.
    /// Used only for catalog metadata; query parameter binding is value-driven.
    /// </summary>
    internal static FieldType MapMySqlType(string mySqlType)
    {
        var parenIndex = mySqlType.IndexOf('(', StringComparison.Ordinal);
        var baseType = (parenIndex >= 0 ? mySqlType[..parenIndex] : mySqlType).Trim();

        return baseType.ToUpperInvariant() switch
        {
            "TINYINT" or "SMALLINT" or "MEDIUMINT" or "INT" or "INTEGER" => FieldType.Integer,
            "BIGINT" or "BIGINT UNSIGNED" => FieldType.BigInteger,
            "FLOAT" => FieldType.Float,
            "DOUBLE" or "DOUBLE PRECISION" or "REAL" or "DECIMAL" or "NUMERIC" => FieldType.Double,
            "BOOLEAN" or "BOOL" or "BIT" => FieldType.Boolean,
            "CHAR" or "VARCHAR" or "TEXT" or "TINYTEXT" or "MEDIUMTEXT" or "LONGTEXT"
                or "ENUM" or "SET" or "NVARCHAR" => FieldType.String,
            "DATE" => FieldType.Date,
            "TIME" => FieldType.Time,
            "DATETIME" or "TIMESTAMP" => FieldType.DateTime,
            "JSON" => FieldType.Json,
            "BINARY" or "VARBINARY" or "BLOB" or "TINYBLOB" or "MEDIUMBLOB" or "LONGBLOB" => FieldType.Binary,
            "GEOMETRY" or "POINT" or "LINESTRING" or "POLYGON"
                or "MULTIPOINT" or "MULTILINESTRING" or "MULTIPOLYGON" or "GEOMETRYCOLLECTION" => FieldType.Geometry,
            _ => FieldType.String
        };
    }
}
