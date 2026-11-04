// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Queries.Filters;
using Honua.Db.Databricks.Features.FeatureStore;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;
using Honua.Db.Databricks.Features.Security;
using Honua.Db.Databricks.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks;

public static class ServiceCollectionExtensions
{
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
        // well-known name without depending on this provider's internals. A standard
        // retry + circuit-breaker policy is attached below so the Statement Execution
        // submit/poll/chunk loop survives transient workspace failures (HTTP 408/429/5xx
        // and transient network faults) with exponential backoff and jitter.
        services
            .AddHttpClient<IDatabricksStatementClient, DatabricksStatementClient>(
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
                })
            .AddHttpResiliencePolicy(DatabricksServiceClientName.ResilienceServiceType, BuildResilienceOptions(options));

        services.AddSingleton<IDatabricksFeatureQueryBuilder, DatabricksFeatureQueryBuilder>();
        services.AddScoped<IDatabricksFeatureDataAccess, DatabricksFeatureDataAccess>();

        services.AddScoped<DatabricksFeatureStore>(sp => new DatabricksFeatureStore(
            sp.GetRequiredService<DatabricksLayerMappingRegistry>(),
            sp.GetRequiredService<IDatabricksFeatureQueryBuilder>(),
            sp.GetRequiredService<IDatabricksFeatureDataAccess>(),
            Honua.Core.Features.FeatureStore.Services.LayerReadSecurityResolver.FromServices(sp)));
        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<DatabricksFeatureStore>());

        // Export the single dialect for this provider (architecture guardrail asserts one
        // dialect per provider). TryAdd so a primary RDBMS provider's dialect wins when
        // Databricks is registered as a secondary backend.
        services.TryAddSingleton<ISqlDialect>(DatabricksSqlDialect.Instance);
        services.AddDatabricksConnectionDriver();

        return services;
    }

    private static ResiliencePolicyOptions BuildResilienceOptions(DatabricksOptions options)
        => new()
        {
            MaxRetryAttempts = options.MaxRetryAttempts,
            BaseDelay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMilliseconds),
            BackoffExponent = 2.0,
            JitterPercentage = 0.2,
        };

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
