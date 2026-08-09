// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Redshift.Features.FeatureStore;
using Honua.Redshift.Features.FeatureStore.Services;
using Honua.Redshift.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Redshift;

/// <summary>
/// Public DI entry point for the read-only Amazon Redshift spatial feature provider (#1712).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redshift feature provider as an additional <see cref="IFeatureDataProvider"/>
    /// in the runtime, alongside the primary backend (PostGIS or DuckDB). Layers whose
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> resolves to the
    /// <c>redshift</c> provider name are routed to this implementation.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration. Reads the <c>Redshift</c> section
    /// to bind <see cref="RedshiftOptions"/> (default connection string and command timeout).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRedshiftFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RedshiftOptions>()
            .Bind(configuration.GetSection("Redshift"))
            .Validate(o => o.CommandTimeoutSeconds > 0, "CommandTimeoutSeconds must be positive.");

        services.AddSingleton<ISqlDialect>(RedshiftSqlDialect.Instance);

        services.AddScoped<IRedshiftConnectionFactory, RedshiftConnectionFactory>();
        services.AddScoped<RedshiftFeatureDataAccess>();
        services.AddScoped<RedshiftFeatureStore>();

        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<RedshiftFeatureStore>());

        // Capability provider for the feature-change transactional outbox. Redshift is read-only in
        // this slice and reports SupportsTransactionalOutbox = false so the server skips dispatcher
        // startup for Redshift-only deployments. Registered with TryAdd so PostgreSQL's
        // true-capability provider wins when both providers are active.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.Redshift.Features.Infrastructure.Events.Outbox.RedshiftOutboxCapabilityProvider>();

        return services;
    }
}
