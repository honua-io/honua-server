// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Databricks.Features.FeatureStore;
using Honua.Databricks.Features.FeatureStore.Services;
using Honua.Databricks.Features.Infrastructure;
using Honua.Databricks.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Databricks;

/// <summary>
/// Public DI entry point for the read-only Databricks feature provider (#1714).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Databricks SQL Statement Execution read-through provider as an
    /// additional <see cref="IFeatureDataProvider"/> in the runtime, alongside the
    /// primary backend (PostGIS, DuckDB, MySQL). Layers whose backing connection
    /// resolves to provider <c>databricks</c> (aliases <c>databrickssql</c>, <c>dbsql</c>)
    /// are routed here through the shared <c>FeatureProviderQueryRouter</c>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration. Reads the <c>Databricks</c>
    /// section to bind <see cref="DatabricksOptions"/> (host, warehouse id, token,
    /// catalog/schema, timeouts, and layer mappings).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddDatabricksFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new DatabricksOptions();
        configuration.GetSection("Databricks").Bind(options);
        DatabricksOptionsValidator.ThrowIfInvalid(options);

        services
            .AddOptions<DatabricksOptions>()
            .Bind(configuration.GetSection("Databricks"))
            .Validate(o =>
            {
                DatabricksOptionsValidator.ThrowIfInvalid(o);
                return true;
            });

        services.AddSingleton(new DatabricksLayerMappingRegistry(BuildLayerMappings(options)));

        // Typed-HttpClient registration: the BaseAddress points at the workspace host so
        // host applications can attach resilience / retry / telemetry handlers via the
        // well-known name without depending on this provider's internals.
        services.AddHttpClient<IDatabricksStatementClient, DatabricksStatementClient>(
            DatabricksServiceClientName.Default,
            client =>
            {
                if (Uri.TryCreate(options.Host, UriKind.Absolute, out var host))
                {
                    client.BaseAddress = host;
                }

                // Generous client timeout; the statement client enforces the finer-grained
                // CommandTimeout budget across the submit/poll loop.
                client.Timeout = TimeSpan.FromSeconds(Math.Max(options.CommandTimeoutSeconds + 30, 60));
            });

        services.AddSingleton<IDatabricksFeatureQueryBuilder, DatabricksFeatureQueryBuilder>();
        services.AddScoped<IDatabricksFeatureDataAccess, DatabricksFeatureDataAccess>();

        services.AddScoped<DatabricksFeatureStore>();
        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<DatabricksFeatureStore>());

        // Export the single dialect for this provider (architecture guardrail asserts one
        // dialect per provider). TryAdd so a primary RDBMS provider's dialect wins when
        // Databricks is registered as a secondary backend.
        services.TryAddSingleton<ISqlDialect>(DatabricksSqlDialect.Instance);

        return services;
    }

    private static List<DatabricksLayerMapping> BuildLayerMappings(DatabricksOptions options)
    {
        var mappings = new List<DatabricksLayerMapping>(options.Layers.Length);
        foreach (var layer in options.Layers)
        {
            var geometryType = Enum.Parse<GeometryType>(layer.GeometryType, ignoreCase: true);
            mappings.Add(new DatabricksLayerMapping
            {
                LayerId = layer.Id,
                Table = layer.Table,
                Catalog = layer.Catalog ?? options.Catalog,
                Schema = layer.Schema ?? options.Schema,
                GeometryColumn = layer.GeometryColumn,
                PrimaryKeyColumn = layer.PrimaryKeyColumn,
                Srid = layer.Srid,
                GeometryType = geometryType,
                AttributeColumns = layer.Attributes,
            });
        }

        return mappings;
    }
}
