// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Snowflake.Features.FeatureStore;
using Honua.Snowflake.Features.FeatureStore.Services;
using Honua.Snowflake.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Snowflake;

/// <summary>
/// Public DI entry point for the Snowflake spatial feature provider (#1713).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Snowflake feature provider as an additional <see cref="IFeatureDataProvider"/>
    /// in the runtime, alongside the primary backend (PostGIS, DuckDB, MySQL). Layers whose
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> resolves to the
    /// <c>snowflake</c>/<c>snowflakedb</c> provider name are routed to this implementation.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration. Reads the <c>Snowflake</c> section
    /// to bind <see cref="SnowflakeOptions"/> (default connection string and command timeout).</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// AOT note: <c>Snowflake.Data</c> uses internal reflection and is not Native AOT-compatible.
    /// Deployments that publish with Native AOT must exclude this provider.
    /// </remarks>
    public static IServiceCollection AddSnowflakeFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SnowflakeOptions>()
            .Bind(configuration.GetSection("Snowflake"))
            .Validate(o => o.CommandTimeoutSeconds > 0, "CommandTimeoutSeconds must be positive.");

        services.AddSingleton<ISqlDialect>(SnowflakeSqlDialect.Instance);

        // Scoped registration (matches Oracle/SQL Server): these services consume
        // ISecureConnectionResolver, which is registered Scoped, so a Singleton lifetime here
        // would be a captive dependency that fails ServiceProvider ValidateOnBuild/ValidateScopes.
        services.AddScoped<ISnowflakeConnectionFactory, SnowflakeConnectionFactory>();
        services.AddScoped<SnowflakeFeatureDataAccess>();
        services.AddScoped<SnowflakeFeatureStore>();

        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<SnowflakeFeatureStore>());

        // Capability provider for the feature-change transactional outbox (#692). Snowflake is
        // read-only in this slice and reports SupportsTransactionalOutbox = false so the server
        // skips dispatcher startup for Snowflake-only deployments. Registered with TryAdd so
        // PostgreSQL's true-capability provider wins when both providers are active.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.Snowflake.Features.Infrastructure.Events.Outbox.SnowflakeOutboxCapabilityProvider>();

        return services;
    }
}
