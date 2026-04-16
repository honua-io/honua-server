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

        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<IWorkflowDefinitionStore>(sp =>
                new RedisWorkflowDefinitionStore(sp.GetRequiredService<IConnectionMultiplexer>()));

            services.TryAddSingleton<IWorkflowRunStore>(sp =>
                new RedisWorkflowRunStore(sp.GetRequiredService<IConnectionMultiplexer>()));
        }

        services.TryAddSingleton<WorkflowOrchestrationEngine>();

        return services;
    }

    public static IServiceCollection AddOrchestrationBackgroundServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<WorkflowOrchestrationBackgroundService>();
        services.AddHostedService<WorkflowSchedulerBackgroundService>();
        return services;
    }
}
