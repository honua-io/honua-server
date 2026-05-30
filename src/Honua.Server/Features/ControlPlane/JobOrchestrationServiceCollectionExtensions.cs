// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.Share;
using Honua.Geoprocessing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.ControlPlane;

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
    /// register heavyweight execution or reconciliation services. It does register
    /// the Share export terminal-reconciliation callback, which the serving image
    /// needs because operator cancel (a terminal transition) is served API-side.
    /// </remarks>
    public static IServiceCollection AddJobOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The Share export run reconciler is an orchestration-level terminal callback, not a
        // worker-only one: a terminal transition also happens API-side when an operator cancels a
        // queued export through ConsoleJobService, and that path must still move the Share run to its
        // terminal state. It is a cheap singleton whose dependencies are always registered, so it
        // registers independently of the Redis-backed queue below. (The geoprocessing terminal
        // callback stays worker-side in AddJobWorker because it depends on the Redis-gated
        // result-package store; its API-side cancel reconciliation is a separate follow-on.)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobTerminalCallback, ShareExportJobTerminalCallback>());

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
    /// from a lean API-only image would introduce execution overhead. The Share
    /// export terminal callback is registered by <see cref="AddJobOrchestration"/>
    /// instead, because the API image also drives terminal transitions (operator
    /// cancel); the geoprocessing terminal callback stays here because of its
    /// Redis-gated dependency.
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

        // The geoprocessing terminal callback depends on the Redis-gated result-package store, so it
        // remains worker-side where that store is present. (The Share callback is registered by
        // AddJobOrchestration because it must also fire on API-side operator cancel.)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobTerminalCallback, GeoprocessingJobTerminalCallback>());

        services.AddHostedService<JobExecutionService>();
        services.AddHostedService<JobReconciliationService>();

        return services;
    }
}
