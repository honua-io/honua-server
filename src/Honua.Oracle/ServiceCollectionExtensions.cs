// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Oracle.Features.FeatureStore;
using Honua.Oracle.Features.FeatureStore.Services;
using Honua.Oracle.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Oracle;

/// <summary>
/// Public DI entry point for the Oracle spatial feature provider (#1252).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Oracle feature provider as an additional <see cref="IFeatureDataProvider"/>
    /// in the runtime, alongside the primary backend (PostGIS, DuckDB, MySQL). Layers whose
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> resolves to the
    /// <c>oracle</c>/<c>oracledb</c> provider name are routed to this implementation.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration. Reads the <c>Oracle</c> section
    /// to bind <see cref="OracleOptions"/> (default connection string and command timeout).</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// AOT note: <c>Oracle.ManagedDataAccess.Core</c> uses internal reflection and is not
    /// Native AOT-compatible. Deployments that publish with Native AOT must exclude this
    /// provider.
    /// </remarks>
    public static IServiceCollection AddOracleFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<OracleOptions>()
            .Bind(configuration.GetSection("Oracle"))
            .Validate(o => o.CommandTimeoutSeconds > 0, "CommandTimeoutSeconds must be positive.");

        services.AddSingleton<ISqlDialect>(OracleSqlDialect.Instance);

        services.AddScoped<IOracleConnectionFactory, OracleConnectionFactory>();
        services.AddScoped<IOracleSpatialMetadataProbe, OracleSpatialMetadataProbe>();
        services.AddScoped<OracleSpatialGuard>();
        services.AddScoped<OracleFeatureDataAccess>();
        services.AddScoped<OracleFeatureStore>();

        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<OracleFeatureStore>());

        // Capability provider for the feature-change transactional outbox (#692). Oracle is
        // read-only in this slice and reports SupportsTransactionalOutbox = false so the
        // server skips dispatcher startup for Oracle-only deployments. Registered with
        // TryAdd so PostgreSQL's true-capability provider wins when both providers are active.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.Oracle.Features.Infrastructure.Events.Outbox.OracleOutboxCapabilityProvider>();

        return services;
    }
}
