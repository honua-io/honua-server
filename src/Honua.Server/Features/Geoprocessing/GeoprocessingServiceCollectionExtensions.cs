// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Server.Features.Geoprocessing.Execution;
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
            services.TryAddSingleton<IGeoprocessingResultPackageStore>(sp =>
                new RedisGeoprocessingResultPackageStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));
        }

        // Execution admission controls (ticket #739) — rate, concurrency, cost, backpressure
        services
            .AddOptions<ExecutionAdmissionOptions>()
            .Bind(configuration.GetSection(ExecutionAdmissionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IExecutionAdmissionEvaluator, ExecutionAdmissionEvaluator>();

        // Shared geoprocessing job service (#723) — consumed by gRPC and REST adapters
        services.TryAddSingleton<IGeoprocessingJobService, GeoprocessingJobService>();

        // Workflow orchestration substrate (#724) — exposes geoprocessing as the
        // canonical job executor consumed by the orchestration engine.
        services.TryAddSingleton<IWorkflowJobExecutor, GeoprocessingWorkflowJobExecutor>();

        // Executor guardrails (ticket #1031): per-job artifact size ceiling and
        // result retention TTL. Bound from configuration with safe defaults so
        // the production executor can run without explicit operator setup.
        services
            .AddOptions<GeoprocessingExecutorOptions>()
            .Bind(configuration.GetSection(GeoprocessingExecutorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Built-in production executors (ticket #1031). Registered as the single
        // IJobExecutor for ExecutionJobKind.Geoprocessing; per-process dispatch
        // happens inside the executor. AddJobWorker activates the host that
        // resolves and invokes these executors.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, GeometryBufferJobExecutor>());

        // Job orchestration substrate: queue, log store (ticket #681)
        services.AddJobOrchestration();

        return services;
    }
}
