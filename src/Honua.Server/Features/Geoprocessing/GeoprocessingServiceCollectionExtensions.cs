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

        // Built-in production executors (ticket #1031). Slice 1 introduced the
        // first concrete executor (geometry.buffer); slice 2 added geometry.clip,
        // geometry.intersect, and geometry.project; slice 3 added geometry.area
        // (per-feature measure) and geometry.union (collection aggregation);
        // slice 4 added geometry.centroid, geometry.length, and
        // geometry.convex-hull — rounding out the deterministic single-feature
        // vector set; slice 5 lands the migration-priority shape transforms
        // geometry.dissolve (group-aware aggregate), geometry.simplify
        // (Douglas-Peucker), and geometry.snap (vertex conditioning) so the
        // workspace covers the common parity targets before later slices
        // tackle the heavyweight raster / surface families. The worker host
        // keys executors by ExecutionJobKind, so the per-process executors
        // are composed behind GeoprocessingDispatchJobExecutor — the single
        // IJobExecutor registered for ExecutionJobKind.Geoprocessing — which
        // routes claimed jobs to the matching handler.
        services.TryAddSingleton<GeometryBufferJobExecutor>();
        services.TryAddSingleton<GeometryClipJobExecutor>();
        services.TryAddSingleton<GeometryIntersectJobExecutor>();
        services.TryAddSingleton<GeometryProjectJobExecutor>();
        services.TryAddSingleton<GeometryAreaJobExecutor>();
        services.TryAddSingleton<GeometryUnionJobExecutor>();
        services.TryAddSingleton<GeometryCentroidJobExecutor>();
        services.TryAddSingleton<GeometryLengthJobExecutor>();
        services.TryAddSingleton<GeometryConvexHullJobExecutor>();
        services.TryAddSingleton<GeometryDissolveJobExecutor>();
        services.TryAddSingleton<GeometrySimplifyJobExecutor>();
        services.TryAddSingleton<GeometrySnapJobExecutor>();

        // Catalog-honesty reconciliation onto #1185: geometry.make-valid and
        // geometry.difference were advertised in the catalog (and flagged
        // synchronous / first-slice automated) on trunk but had no executor, so
        // a job submission could never reach them. Add the managed NTS executors
        // (GeometryFixer / Geometry.Difference) so the advertised set is honest.
        services.TryAddSingleton<GeometryMakeValidJobExecutor>();
        services.TryAddSingleton<GeometryDifferenceJobExecutor>();

        // GeoETL transform executors reconciled from feat/geoetl-baseline onto the
        // #1185 add-a-capability contract. Each reads/writes a FeatureCollection
        // data URI and is composed behind GeoprocessingDispatchJobExecutor.
        services.TryAddSingleton<AttributeRenameTransformExecutor>();
        services.TryAddSingleton<AttributeCastTransformExecutor>();
        services.TryAddSingleton<ComputedFieldTransformExecutor>();
        services.TryAddSingleton<AttributeFilterTransformExecutor>();
        services.TryAddSingleton<SpatialFilterTransformExecutor>();
        services.TryAddSingleton<ClipTransformExecutor>();
        services.TryAddSingleton<DedupTransformExecutor>();
        services.TryAddSingleton<ReprojectTransformExecutor>();
        services.TryAddSingleton<GeoJsonSourceExecutor>();
        services.TryAddSingleton<CsvSourceExecutor>();
        services.TryAddSingleton<GeoJsonFileSinkExecutor>();
        services.TryAddSingleton<QuarantineSinkExecutor>();
        services.TryAddSingleton<ExternalPostgisSinkExecutor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, GeoprocessingDispatchJobExecutor>());

        // Job orchestration substrate: queue, log store (ticket #681)
        services.AddJobOrchestration();

        return services;
    }
}
