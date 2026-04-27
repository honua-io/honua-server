// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.DuckDB.Features.Catalog;
using Honua.DuckDB.Features.FeatureStore;
using Honua.DuckDB.Features.FeatureStore.Services;
using DuckDB.NET.Data;
using Honua.DuckDB.Features.Infrastructure;
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
        services.AddScoped<IFeatureWriter>(_ => new ReadOnlyFeatureWriter());
        services.AddScoped<IReplicaRepository>(_ => new ReadOnlyReplicaRepository());
        services.AddScoped<IChangeTracker>(_ => new ReadOnlyChangeTracker());
        services.AddScoped<IGeoJsonFeatureStore>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<IStreamingFeatureStore>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<ITileProvider>(_ => new ReadOnlyTileProvider());
        services.AddScoped<IGmlFeatureStore>(_ => new ReadOnlyGmlFeatureStore());

        // Register catalog (scoped, from configuration + discovered column types)
        services.AddScoped<ILayerCatalog>(sp =>
        {
            var registry = sp.GetRequiredService<DuckDBLayerRegistry>();
            var (layers, serviceDefs) = BuildCatalogEntries(options, registry.Mappings);
            return new DuckDBLayerCatalog(layers, serviceDefs, sp.GetRequiredService<ILogger<DuckDBLayerCatalog>>());
        });

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

    private static (List<LayerDefinition> Layers, List<ServiceDefinition> Services) BuildCatalogEntries(
        DuckDBOptions options, IEnumerable<DuckDBLayerMapping> mappings)
    {
        var mappingsByLayerId = mappings.ToDictionary(m => m.LayerId);
        var layers = new List<LayerDefinition>(options.Layers.Length);
        var layerMap = new Dictionary<int, LayerDefinition>();

        foreach (var layerOpt in options.Layers)
        {
            var geometryType = Enum.TryParse<GeometryType>(layerOpt.GeometryType, ignoreCase: true, out var gt)
                ? gt
                : GeometryType.Point;

            var srs = SpatialReference.Create(layerOpt.Srid);

            var fields = new List<FieldDefinition>
            {
                new(Core.Features.Shared.Models.FieldNames.ObjectId, FieldType.Integer, Nullable: false, Description: "Unique object identifier")
            };

            if (geometryType != GeometryType.None)
            {
                fields.Add(new("shape", FieldType.Geometry, Nullable: false, Description: "Geometry field"));
            }

            // Add attribute column fields from discovered/configured column metadata
            if (mappingsByLayerId.TryGetValue(layerOpt.Id, out var mapping))
            {
                foreach (var attrCol in mapping.AttributeColumns)
                {
                    var fieldType = mapping.AttributeColumnTypes.TryGetValue(attrCol, out var duckDbType)
                        ? MapDuckDBType(duckDbType)
                        : FieldType.String;
                    fields.Add(new(attrCol, fieldType));
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
                    PrimaryKeyColumn: layerOpt.ObjectIdColumn,
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

            var service = new ServiceDefinition(
                svcOpt.Name,
                svcOpt.Description ?? $"DuckDB feature service: {svcOpt.Name}",
                svcLayers,
                svcLayers[0].SpatialReference,
                Capabilities: svcOpt.Capabilities,
                Metadata: metadata);

            services.Add(service);
        }

        return (layers, services);
    }

    /// <summary>
    /// Maps a DuckDB column type string (from PRAGMA table_info) to a <see cref="FieldType"/>.
    /// </summary>
    internal static FieldType MapDuckDBType(string duckDbType)
    {
        // Strip precision/scale suffix, e.g. "DECIMAL(10,2)" → "DECIMAL"
        var parenIndex = duckDbType.IndexOf('(');
        var baseType = (parenIndex >= 0 ? duckDbType[..parenIndex] : duckDbType).Trim();

        return baseType.ToUpperInvariant() switch
        {
            "INTEGER" or "INT" or "INT4" or "SIGNED"
                or "SMALLINT" or "INT2" or "SHORT"
                or "TINYINT" or "INT1"
                or "UTINYINT" or "USMALLINT" => FieldType.Integer,

            "BIGINT" or "INT8" or "LONG" or "HUGEINT"
                or "UINTEGER" or "UBIGINT" => FieldType.BigInteger,

            "FLOAT" or "REAL" or "FLOAT4" => FieldType.Float,
            "DOUBLE" or "FLOAT8" or "DECIMAL" or "NUMERIC" => FieldType.Double,
            "BOOLEAN" or "BOOL" or "LOGICAL" => FieldType.Boolean,

            "VARCHAR" or "TEXT" or "STRING"
                or "CHAR" or "BPCHAR" or "NVARCHAR" => FieldType.String,

            "DATE" => FieldType.Date,
            "TIME" or "TIMETZ" or "TIME WITH TIME ZONE" => FieldType.Time,

            "TIMESTAMP" or "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE"
                or "TIMESTAMP_S" or "TIMESTAMP_MS" or "TIMESTAMP_NS"
                or "DATETIME" => FieldType.DateTime,

            "UUID" => FieldType.Uuid,
            "BLOB" or "BYTEA" or "VARBINARY" => FieldType.Binary,
            "JSON" => FieldType.Json,
            "GEOMETRY" => FieldType.Geometry,

            _ => FieldType.String,
        };
    }
}
