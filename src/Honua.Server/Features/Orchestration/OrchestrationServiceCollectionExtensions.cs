// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Watermark;
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

        // Durable per-pipeline+source high-water marks for incremental ("changed-since") extract.
        services.TryAddSingleton<ISourceWatermarkStore>(sp =>
            new RedisSourceWatermarkStore(sp.GetRequiredService<IConnectionMultiplexer>()));

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

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Key) && !string.IsNullOrWhiteSpace(child.Value))
            {
                roots[child.Key] = child.Value!;
            }
        }

        return roots;
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
        services.AddHostedService<WorkflowEventTriggerBackgroundService>();
        return services;
    }
}
