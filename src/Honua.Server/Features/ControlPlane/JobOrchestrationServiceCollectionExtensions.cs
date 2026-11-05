// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.Share;
using Honua.Geoprocessing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.ControlPlane;

internal static class JobOrchestrationServiceCollectionExtensions
{
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

        // Ops-notification producer: emits an Error-severity ops notification when a job
        // fails. Cheap singleton with always-registered deps, fired from every terminal
        // loop (worker and API-side cancel); no-ops when ops notifications are disabled.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobTerminalCallback, Honua.Alerts.Ops.OpsJobTerminalCallback>());

        // Job store is already registered by AddGeoprocessing; guard for idempotency.
        // Queue and log store require Redis.
        if (!services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer))
            || !services.Any(d => d.ServiceType == typeof(IExecutionJobStore)))
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

    // The Share export callback is registered on the API image because operator cancel is served there, and event mode does not host the in-process timers.
    public static IServiceCollection AddJobWorker(this IServiceCollection services)
        => services.AddJobWorker(new ConfigurationBuilder().Build());

    public static IServiceCollection AddJobWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

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

        // JobExecutionService is the always-on queue drainer, not a periodic tick; it stays hosted
        // in both trigger modes on the API and on long-lived hosts. An event-handler Lambda
        // already runs one reconcile or tick per invocation and then releases its pool; a second
        // drainer on a pool of 2 pins connections across freeze (honua-server#6515).
        if (ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            services.AddHostedService<JobExecutionService>();
        }

        // JobReconciliationService is the PERIODIC heartbeat/timeout reaping sweep (bucket-b). Its
        // sweep body is idempotent (per-candidate re-read + optimistic CAS), so the scheduled-tick
        // handler is registered in BOTH modes; the in-process 30s timer is hosted only under
        // TriggerMode=Poll (default, on-prem), keeping that path byte-for-byte unchanged.
        services.TryAddSingleton<JobReconciliationService>();
        services.AddSingleton<IScheduledTickHandler, JobReconciliationScheduledTickHandler>();
        if (ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration))
        {
            services.AddHostedService(sp => sp.GetRequiredService<JobReconciliationService>());
        }

        return services;
    }
}
