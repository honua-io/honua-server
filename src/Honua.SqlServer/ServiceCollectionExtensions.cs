// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.SqlServer.Features.FeatureStore;
using Honua.SqlServer.Features.FeatureStore.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.SqlServer;

/// <summary>
/// Public DI entry point for the SQL Server spatial feature provider (#850).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server feature provider as an additional <see cref="IFeatureDataProvider"/>
    /// in the runtime, alongside the primary backend (PostGIS or DuckDB). Layers whose
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> resolves to the
    /// <c>sqlserver</c>/<c>mssql</c> provider name are routed to this implementation.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration. Reads the <c>SqlServer</c> section
    /// to bind <see cref="SqlServerOptions"/> (default connection string and command timeout).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SqlServerOptions>()
            .Bind(configuration.GetSection("SqlServer"))
            .Validate(o => o.CommandTimeoutSeconds > 0, "CommandTimeoutSeconds must be positive.");

        services.AddScoped<ISqlServerConnectionFactory, SqlServerConnectionFactory>();
        services.AddScoped<SqlServerFeatureDataAccess>();
        services.AddScoped<SqlServerFeatureStore>();

        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<SqlServerFeatureStore>());

        // Capability provider for the feature-change transactional outbox (#692). SQL Server
        // is read-only in this slice and reports SupportsTransactionalOutbox = false so the
        // server skips dispatcher startup for SQL-Server-only deployments. Registered with
        // TryAdd so PostgreSQL's true-capability provider wins when both providers are active.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.SqlServer.Features.Infrastructure.Events.Outbox.SqlServerOutboxCapabilityProvider>();

        return services;
    }
}
