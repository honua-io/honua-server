// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Registers orchestration feature services.
/// </summary>
internal static class OrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // The orchestration engine requires durable stores. Without Redis-backed
        // IWorkflowDefinitionStore/IWorkflowRunStore the engine cannot activate, so
        // skip registering it. Admin cancel then resolves IWorkflowCancellationCoordinator
        // to null and returns the documented 503 Service Unavailable instead of a 500
        // from a DI activation failure.
        if (!services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            return services;
        }

        services.TryAddSingleton<IWorkflowDefinitionStore>(sp =>
            new RedisWorkflowDefinitionStore(sp.GetRequiredService<IConnectionMultiplexer>()));

        services.TryAddSingleton<IWorkflowRunStore>(sp =>
            new RedisWorkflowRunStore(sp.GetRequiredService<IConnectionMultiplexer>()));

        services.TryAddSingleton<WorkflowOrchestrationEngine>();
        services.TryAddSingleton<IWorkflowCancellationCoordinator>(sp =>
            sp.GetRequiredService<WorkflowOrchestrationEngine>());

        return services;
    }

    public static IServiceCollection AddOrchestrationBackgroundServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Background services depend on the same Redis-backed stores the engine needs.
        // Only start them when AddOrchestration actually registered the engine — otherwise
        // hosted-service activation would fail at startup in Redis-less deployments.
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

        return services;
    }
}
