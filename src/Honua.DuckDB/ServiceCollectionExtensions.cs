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

        // Validate and strip write capabilities at startup
        foreach (var svc in options.Services)
        {
            var originalCaps = svc.Capabilities;
            svc.Capabilities = originalCaps
                .Where(c => !c.Equals("Create", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Update", StringComparison.OrdinalIgnoreCase) &&
                            !c.Equals("Delete", StringComparison.OrdinalIgnoreCase))
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
                sp.GetRequiredService<ILogger<DuckDBSpatialBootstrap>>()));

        services.AddSingleton<DuckDBLayerRegistry>(sp =>
        {
            var mappings = BuildLayerMappings(options, sp);
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
        services.AddScoped<IFeatureReader>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<IFeatureWriter>(_ => new ReadOnlyFeatureWriter());
        services.AddScoped<IGeoJsonFeatureStore>(sp => sp.GetRequiredService<DuckDBFeatureStore>());
        services.AddScoped<IStreamingFeatureStore>(sp => sp.GetRequiredService<DuckDBFeatureStore>());

        // Register catalog (scoped, from configuration)
        services.AddScoped<ILayerCatalog>(sp =>
        {
            var (layers, serviceDefs) = BuildCatalogEntries(options);
            return new DuckDBLayerCatalog(layers, serviceDefs, sp.GetRequiredService<ILogger<DuckDBLayerCatalog>>());
        });

        return services;
    }

    private static List<DuckDBLayerMapping> BuildLayerMappings(DuckDBOptions options, IServiceProvider sp)
    {
        var mappings = new List<DuckDBLayerMapping>(options.Layers.Length);
        var logger = sp.GetRequiredService<ILogger<DuckDBLayerRegistry>>();

        foreach (var layerOpt in options.Layers)
        {
            // Discover attribute columns: all fields from config except geometry and object ID
            // In a future iteration, this could introspect DuckDB schema at startup
            var attributeColumns = new List<string>();

            logger.LogInformation(
                "Registered DuckDB layer {LayerId}: table={Table}, geom={GeomCol}, oid={OidCol}",
                layerOpt.Id, layerOpt.Table, layerOpt.GeometryColumn, layerOpt.ObjectIdColumn);

            mappings.Add(new DuckDBLayerMapping
            {
                LayerId = layerOpt.Id,
                TableName = layerOpt.Table,
                GeometryColumn = layerOpt.GeometryColumn,
                ObjectIdColumn = layerOpt.ObjectIdColumn,
                Srid = layerOpt.Srid,
                AttributeColumns = attributeColumns
            });
        }

        return mappings;
    }

    private static (List<LayerDefinition> Layers, List<ServiceDefinition> Services) BuildCatalogEntries(DuckDBOptions options)
    {
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

            var layer = new LayerDefinition(
                layerOpt.Id,
                layerOpt.Name,
                layerOpt.Description,
                geometryType,
                srs,
                fields.ToArray(),
                SupportsAttachments: false);

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
}
