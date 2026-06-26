// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.WorkflowPackages;

internal static class WorkflowPackageServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowPackages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Mirror AddOrchestration's durable-store selection: when Redis durable
        // coordination is available, packages/versions/publications persist in Redis so
        // restarts and sibling replicas share one registry — keeping publication records
        // alive for the durable cron WorkflowDefinitions that schedule publications
        // create. Without Redis (single-node), keep the process-local in-memory store;
        // the orchestration engine is not registered in that mode either, so no durable
        // definitions can outlive their owning publication.
        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<IWorkflowPackageStore>(sp =>
                new RedisWorkflowPackageStore(sp.GetRequiredService<IConnectionMultiplexer>()));
        }
        else
        {
            services.TryAddSingleton<InMemoryWorkflowPackageStore>();
            services.TryAddSingleton<IWorkflowPackageStore>(sp =>
                sp.GetRequiredService<InMemoryWorkflowPackageStore>());
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeProvider, ProcessCatalogWorkflowNodeProvider>());
        services.TryAddSingleton<IWorkflowNodeRegistry, WorkflowNodeRegistry>();
        services.TryAddScoped(sp => new WorkflowPackageService(
            sp.GetRequiredService<IWorkflowPackageStore>(),
            sp.GetRequiredService<IWorkflowNodeRegistry>(),
            sp.GetRequiredService<IGeoprocessingJobService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WorkflowPackageService>>(),
            sp.GetService<IWorkflowDefinitionStore>(),
            sp.GetService<WorkflowOrchestrationEngine>()));

        return services;
    }
}
