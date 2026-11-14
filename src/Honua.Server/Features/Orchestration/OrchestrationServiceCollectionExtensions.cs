// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Migration.Watermark;
using Honua.Core.Features.Orchestration.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Orchestration;

internal static class OrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<PostgresWorkflowDefinitionStore>();
        services.TryAddSingleton<IWorkflowDefinitionStore>(sp => sp.GetRequiredService<PostgresWorkflowDefinitionStore>());
        services.TryAddSingleton<PostgresWorkflowRunStore>();
        services.TryAddSingleton<IWorkflowRunStore>(sp => sp.GetRequiredService<PostgresWorkflowRunStore>());

        services.TryAddSingleton<WorkflowOrchestrationEngine>();
        services.TryAddSingleton<IWorkflowCancellationCoordinator>(sp =>
            sp.GetRequiredService<WorkflowOrchestrationEngine>());

        // Durable per-pipeline+source high-water marks for incremental ("changed-since") extract.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<ISourceWatermarkStore>(sp =>
                new RedisSourceWatermarkStore(sp.GetRequiredService<IConnectionMultiplexer>()));
        }

        // Event/CDC trigger probes. The change-feed probe adapts the scoped IChangeTracker; the
        // object-store probe is a poll-based file-system baseline whose StoreId->root bindings come
        // from configuration (Orchestration:ObjectStoreTriggers:Roots:<storeId> = <path>). Bindings
        // are read manually (no reflection binder) to stay AOT-safe.
        services.TryAddSingleton<IChangeFeedGenerationProbe, ChangeTrackerGenerationProbe>();
        services.TryAddSingleton<IObjectStoreTriggerProbe>(sp =>
            new FileSystemObjectStoreTriggerProbe(
                ReadObjectStoreRoots(sp.GetService<IConfiguration>())));

        return services;
    }

    private static Dictionary<string, string> ReadObjectStoreRoots(IConfiguration? configuration)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = configuration?.GetSection("Orchestration:ObjectStoreTriggers:Roots");
        if (section is null || !section.Exists())
        {
            return roots;
        }

        foreach (var child in section.GetChildren()
                     .Where(child => !string.IsNullOrWhiteSpace(child.Key) && !string.IsNullOrWhiteSpace(child.Value)))
        {
            roots[child.Key] = child.Value!;
        }

        return roots;
    }

    public static IServiceCollection AddOrchestrationBackgroundServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!services.Any(d => d.ServiceType == typeof(WorkflowOrchestrationEngine)))
        {
            return services;
        }

        services.AddHostedService<WorkflowOrchestrationBackgroundService>();

        // Cron scheduler tick: a single shared singleton instance so the in-memory compiled-cron
        // cache survives across ticks whether the in-process timer or the scheduled-tick dispatcher
        // drives it. The IScheduledTickHandler is registered in BOTH trigger modes so EventBridge
        // Scheduler can drive the tick under TriggerMode=Event; the in-process timer is hosted only
        // under TriggerMode=Poll (default, on-prem), keeping that path byte-for-byte unchanged.
        services.TryAddSingleton<WorkflowSchedulerBackgroundService>();
        services.AddSingleton<IScheduledTickHandler, WorkflowSchedulerScheduledTickHandler>();
        if (ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration))
        {
            services.AddHostedService(sp => sp.GetRequiredService<WorkflowSchedulerBackgroundService>());
        }

        // Event/CDC trigger reconciler (object-store + change-feed probes). The change-feed
        // probe reads Postgres. An event-handler Lambda must not keep that poller: it would
        // resume on the next thaw and hold a connection after the invocation releases the pool.
        // The scheduled-tick handler above still runs the cron tick when EventBridge asks.
        if (ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            services.AddHostedService<WorkflowEventTriggerBackgroundService>();
        }

        return services;
    }
}
