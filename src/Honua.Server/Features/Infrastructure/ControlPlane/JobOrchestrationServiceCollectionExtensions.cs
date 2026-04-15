// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Registers durable job orchestration services, separated into API-side and
/// worker-side concerns to preserve a lean serving runtime. The API image
/// registers only <see cref="AddJobOrchestration"/>; the worker image
/// additionally registers <see cref="AddJobWorker"/>.
/// </summary>
internal static class JobOrchestrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers API-side job orchestration dependencies: the durable job store,
    /// job queue, and execution log store. These are shared between the API
    /// serving path and background workers.
    /// </summary>
    /// <remarks>
    /// This method is safe to call from the default serving image. It does not
    /// register heavyweight execution or reconciliation services.
    /// </remarks>
    public static IServiceCollection AddJobOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Job store is already registered by AddGeoprocessing; guard for idempotency.
        // Queue and log store require Redis.
        if (!services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            return services;
        }

        services.TryAddSingleton<RedisJobQueue>(sp =>
            new RedisJobQueue(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IExecutionJobStore>(),
                sp.GetRequiredService<ILogger<RedisJobQueue>>()));

        services.TryAddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
        services.TryAddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());

        services.TryAddSingleton<IExecutionLogStore>(sp =>
            new RedisExecutionLogStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));

        return services;
    }

    /// <summary>
    /// Registers worker-side job execution services: the execution host that
    /// claims and runs jobs, and the reconciliation service that detects expired
    /// heartbeats and applies retry policies.
    /// </summary>
    /// <remarks>
    /// This method registers <see cref="BackgroundService"/> implementations that
    /// should only be activated in a worker or combined-mode host. Calling this
    /// from a lean API-only image would introduce execution overhead.
    /// </remarks>
    public static IServiceCollection AddJobWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Requires orchestration services to be registered first.
        if (!services.Any(d => d.ServiceType == typeof(IJobQueue)))
        {
            return services;
        }

        services.TryAddSingleton<ExecutionJobCancellationTokens>();
        services.AddSingleton<IJobCancellationNotifier>(
            sp => sp.GetRequiredService<ExecutionJobCancellationTokens>());

        services.AddSingleton<IJobTerminalCallback, GeoprocessingJobTerminalCallback>();

        services.AddHostedService<JobExecutionService>();
        services.AddHostedService<JobReconciliationService>();

        return services;
    }
}
