// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
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

        // Provider-internal ADO.NET escape hatch (ADR 0046): forwards to the
        // registered provider instance.
        services.AddScoped<IAdoNetDatabaseConnectionProvider>(sp =>
            (IAdoNetDatabaseConnectionProvider)sp.GetRequiredService<IDatabaseConnectionProvider>());

        // Audit-C3 session abstraction registered alongside the legacy provider
        // during the progressive migration (see ADR 0046).
        services.AddScoped<IDatabaseSessionFactory>(sp =>
            new Features.Infrastructure.Session.MySqlDatabaseSessionFactory(
                sp.GetRequiredService<IAdoNetDatabaseConnectionProvider>()));

        services.AddScoped<IFeatureQueryBuilder>(sp =>
            new MySqlFeatureQueryBuilder(
                sp.GetRequiredService<MySqlLayerMappingRegistry>(),
                sp.GetRequiredService<MySqlEngineFlavorHolder>().Flavor));

        services.AddScoped<IFeatureDataAccess>(sp =>
            new MySqlFeatureDataAccess(
                sp.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
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

        // Register the main feature store composition, wiring optional permanent-filter
        // dependencies so row-visibility filters are enforced on MySQL/MariaDB reads.
        services.AddScoped<MySqlFeatureStore>(sp => new MySqlFeatureStore(
            sp.GetRequiredService<IFeatureQueryBuilder>(),
            sp.GetRequiredService<IFeatureDataAccess>(),
            sp.GetService<Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider>(),
            sp.GetService<Honua.Core.Queries.Filters.IFilterExpressionService>()));
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
        services.AddScoped<IReplicaConflictRepository>(_ => new NoOpReplicaConflictRepository());
        services.AddScoped<IChangeTracker>(_ => new NoOpChangeTracker());
        services.AddScoped<IVersionManager>(_ => new NoOpVersionManager());
        // Honua.Infrastructure.Services.SpatialReferenceResolver (a mandatory scoped
        // dependency of FeatureServer/ImageServer/GeometryService/gRPC) requires
        // ICrsDetectionService regardless of provider. Only Postgres ever registered one
        // (CRS detection from WKT/.prj/GeoJSON needs its spatial_ref_sys catalog), so every
        // FeatureServer query under DataSource:Provider=mysql failed DI activation outright
        // before this fix (honua-server#2947).
        services.AddScoped<ICrsDetectionService>(_ => new NoOpCrsDetectionService());
        // Same gap, same fix shape: SpatialReferenceResolver also requires ICrsRegistry.
        // WellKnownCrsRegistry covers CRS84/EPSG:4326/EPSG:3857 (the practical case for a
        // provider with no spatial_ref_sys-equivalent catalog) and honestly reports
        // "unsupported" for anything else.
        services.AddScoped<ICrsRegistry>(_ => new WellKnownCrsRegistry());
        // FeatureProviderQueryRouter (wired unconditionally in
        // InfrastructureCompositionRoot, regardless of primary provider) requires
        // ISecureConnectionRegistry to resolve any publication's storage-binding
        // connection. MySql has no honua.data_connections-equivalent catalog of its
        // own; a process-local in-memory registry keeps DI activation working and
        // supports a secondary/additional provider (e.g. SQL Server) layered on top
        // (honua-server#2947).
        services.AddSingleton<ISecureConnectionRegistry, InMemorySecureConnectionRegistry>();
        // Honua.Protocols.OData's ODataSearchService (wired unconditionally regardless of
        // provider) requires IRelationshipStore; MySql/MariaDB documents relationship
        // queries as unsupported (honua-server#2947).
        services.AddScoped<IRelationshipStore>(_ => new ReadOnlyRelationshipStore("MySQL/MariaDB"));
        // OGC API Features' shared geometry services (mandatory, wired unconditionally
        // regardless of provider) require ICoordinateTransformService (honua-server#2947).
        services.AddSingleton<ICoordinateTransformService>(_ => new WellKnownCoordinateTransformService());
        services.AddScoped<ITileProvider>(_ => new ReadOnlyTileProvider("MySQL/MariaDB"));
        services.AddScoped<IGmlFeatureStore>(_ => new ReadOnlyGmlFeatureStore("MySQL/MariaDB"));
        // The FeatureServer edit pipeline's GeometryValidator (wired unconditionally by
        // Honua.Protocols.GeoServices regardless of provider) requires
        // IGeometryTopologyValidator; only Postgres registers a real (ST_IsValid-backed)
        // one. Without this stub every applyEdits request under DataSource:Provider=mysql
        // failed DI activation with an opaque 500 instead of the documented read-only
        // write rejection (honua-server#2983).
        services.AddScoped<IGeometryTopologyValidator>(_ => new ReadOnlyGeometryTopologyValidator("MySQL/MariaDB"));

        services.AddSingleton<ISqlDialect>(MySqlSqlDialect.Instance);

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
