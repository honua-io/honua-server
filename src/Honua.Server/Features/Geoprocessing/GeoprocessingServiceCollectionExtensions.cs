// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Registers geoprocessing workspace lifecycle and service dependencies.
/// </summary>
internal static class GeoprocessingServiceCollectionExtensions
{
    /// <summary>
    /// Registers geoprocessing service dependencies including workspace lifecycle
    /// and the execution job store.
    /// </summary>
    public static IServiceCollection AddGeoprocessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Workspace lifecycle (ticket #725)
        services.Configure<WorkspaceOptions>(
            configuration.GetSection(WorkspaceOptions.SectionName));

        services.AddSingleton<IRetentionPolicyEvaluator, RetentionPolicyEvaluator>();
        services.AddScoped<IWorkspaceLifecycleService, WorkspaceLifecycleService>();
        services.AddHostedService<WorkspaceCleanupService>();

        // Execution job store (ticket #722)
        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<IExecutionJobStore>(sp =>
                new RedisExecutionJobStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));
        }

        return services;
    }
}
