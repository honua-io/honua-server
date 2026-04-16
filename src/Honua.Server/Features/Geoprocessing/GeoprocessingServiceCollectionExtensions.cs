// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Server.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Registers geoprocessing workspace lifecycle and service dependencies.
/// </summary>
internal static class GeoprocessingServiceCollectionExtensions
{
    /// <summary>
    /// Registers geoprocessing service dependencies including workspace lifecycle,
    /// the execution job store, and built-in process catalog.
    /// </summary>
    public static IServiceCollection AddGeoprocessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Workspace lifecycle (ticket #725)
        services
            .AddOptions<WorkspaceOptions>()
            .Bind(configuration.GetSection(WorkspaceOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<WorkspaceOptions>, WorkspaceOptionsValidator>();

        services.AddSingleton<IRetentionPolicyEvaluator, RetentionPolicyEvaluator>();
        services.AddSingleton<IConfigurationDocumentationContributor, GeoprocessingConfigurationDocumentationContributor>();
        services.TryAddSingleton(TimeProvider.System);

        // Lifecycle orchestration and cleanup require concrete store implementations.
        // Guard registration so the hosted service does not throw at startup when
        // IWorkspaceStore / IArtifactStore are not yet provided by a storage provider.
        if (services.Any(d => d.ServiceType == typeof(IWorkspaceStore))
            && services.Any(d => d.ServiceType == typeof(IArtifactStore)))
        {
            services.AddScoped<IWorkspaceLifecycleService, WorkspaceLifecycleService>();
            services.AddHostedService<WorkspaceCleanupService>();
        }

        // Built-in process catalog (ticket #735)
        services.TryAddSingleton<IProcessCatalog, BuiltInProcessCatalog>();

        // Execution job store (ticket #722)
        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<IExecutionJobStore>(sp =>
                new RedisExecutionJobStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));
        }

        // Shared geoprocessing job service (#723) — consumed by gRPC and REST adapters
        services.TryAddSingleton<IGeoprocessingJobService, GeoprocessingJobService>();

        // Job orchestration substrate: queue, log store (ticket #681)
        services.AddJobOrchestration();

        return services;
    }
}
