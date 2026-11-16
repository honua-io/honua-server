// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Oracle.Features.FeatureStore;
using Honua.Db.Oracle.Features.FeatureStore.Services;
using Honua.Db.Oracle.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Db.Oracle;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOracleFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<OracleOptions>()
            .Bind(configuration.GetSection("Oracle"))
            .Validate(o => o.CommandTimeoutSeconds > 0, "CommandTimeoutSeconds must be positive.");

        services.AddSingleton<ISqlDialect>(OracleSqlDialect.Instance);

        // Scoped registration (matches trunk): these services consume ISecureConnectionResolver,
        // which is registered Scoped, so a Singleton lifetime here is a captive dependency that
        // fails ServiceProvider ValidateOnBuild/ValidateScopes. The #1567 singleton optimisation
        // (avoid per-request OracleSpatialGuard cache resets / extra catalog connections) is
        // deferred to #1593 — it must resolve ISecureConnectionResolver via IServiceScopeFactory
        // before these can become singletons.
        services.AddScoped<IOracleConnectionFactory, OracleConnectionFactory>();
        services.AddScoped<IOracleSpatialMetadataProbe, OracleSpatialMetadataProbe>();
        services.AddScoped<OracleSpatialGuard>();
        services.AddScoped<OracleFeatureDataAccess>();
        services.AddScoped<OracleFeatureStore>(sp => new OracleFeatureStore(
            sp.GetRequiredService<OracleFeatureDataAccess>(),
            sp.GetRequiredService<OracleSpatialGuard>(),
            sp.GetService<Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider>(),
            Honua.Core.Features.FeatureStore.Services.LayerReadSecurityResolver.FromServices(sp),
            sp.GetService<IFilterExpressionService>()));

        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<OracleFeatureStore>());

        // Capability provider for the feature-change transactional outbox (#692). Oracle is
        // read-only in this slice and reports SupportsTransactionalOutbox = false so the
        // server skips dispatcher startup for Oracle-only deployments. Registered with
        // TryAdd so PostgreSQL's true-capability provider wins when both providers are active.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.Db.Oracle.Features.Infrastructure.Events.Outbox.OracleOutboxCapabilityProvider>();

        return services;
    }
}
