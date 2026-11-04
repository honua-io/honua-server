// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.SqlServer.Features.FeatureStore;
using Honua.Db.SqlServer.Features.FeatureStore.Services;
using Honua.Db.SqlServer.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Db.SqlServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SqlServerOptions>()
            .Bind(configuration.GetSection("SqlServer"))
            .Validate(o => o.CommandTimeoutSeconds > 0, "CommandTimeoutSeconds must be positive.");

        services.AddSingleton<ISqlDialect>(SqlServerSqlDialect.Instance);

        services.AddScoped<ISqlServerConnectionFactory, SqlServerConnectionFactory>();
        services.AddScoped<SqlServerFeatureDataAccess>();
        services.AddScoped<SqlServerFeatureStore>(sp => new SqlServerFeatureStore(
            sp.GetRequiredService<SqlServerFeatureDataAccess>(),
            sp.GetService<Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider>(),
            Honua.Core.Features.FeatureStore.Services.LayerReadSecurityResolver.FromServices(sp),
            sp.GetService<IFilterExpressionService>()));

        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<SqlServerFeatureStore>());

        // Capability provider for the feature-change transactional outbox (#692). SQL Server
        // is read-only in this slice and reports SupportsTransactionalOutbox = false so the
        // server skips dispatcher startup for SQL-Server-only deployments. Registered with
        // TryAdd so PostgreSQL's true-capability provider wins when both providers are active.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.Db.SqlServer.Features.Infrastructure.Events.Outbox.SqlServerOutboxCapabilityProvider>();

        return services;
    }
}
