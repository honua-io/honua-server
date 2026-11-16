// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Redshift.Features.FeatureStore;
using Honua.Db.Redshift.Features.FeatureStore.Services;
using Honua.Db.Redshift.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Db.Redshift;

public static class ServiceCollectionExtensions
{
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
        services.AddScoped<RedshiftFeatureStore>(sp => new RedshiftFeatureStore(
            sp.GetRequiredService<RedshiftFeatureDataAccess>(),
            Honua.Core.Features.FeatureStore.Services.LayerReadSecurityResolver.FromServices(sp)));

        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<RedshiftFeatureStore>());

        // Capability provider for the feature-change transactional outbox. Redshift is read-only in
        // this slice and reports SupportsTransactionalOutbox = false so the server skips dispatcher
        // startup for Redshift-only deployments. Registered with TryAdd so PostgreSQL's
        // true-capability provider wins when both providers are active.
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
            Honua.Db.Redshift.Features.Infrastructure.Events.Outbox.RedshiftOutboxCapabilityProvider>();

        return services;
    }
}
