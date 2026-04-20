// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Abstractions;
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

    public static IServiceCollection AddOrchestrationBackgroundServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Background services depend on the same Redis-backed stores the engine needs.
        // Only start them when AddOrchestration actually registered the engine — otherwise
        // hosted-service activation would fail at startup in Redis-less deployments.
        if (!services.Any(d => d.ServiceType == typeof(WorkflowOrchestrationEngine)))
        {
            return services;
        }

        services.AddHostedService<WorkflowOrchestrationBackgroundService>();
        services.AddHostedService<WorkflowSchedulerBackgroundService>();
        return services;
    }
}
