// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.Execution;
using Honua.Geoprocessing.LocalRunner;
using Honua.Infrastructure.Abstractions;
using Honua.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Geoprocessing;

/// <summary>
/// Registers geoprocessing workspace lifecycle and service dependencies.
/// </summary>
internal static class GeoprocessingServiceCollectionExtensions
{
    /// <summary>
    /// Source process ids served by a per-process <see cref="RemoteSourceExecutor"/>.
    /// Must match the catalog ProcessDefinitions and the registered
    /// <c>IDagFeatureSource.SourceId</c> values.
    /// </summary>
    private static readonly string[] RemoteSourceProcessIds =
    [
        "source.honua-layer",
        "source.esri-featureserver",
        "source.ogc-features",
        "source.wfs",
        "source.postgis",
    ];

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

            // Workspace cleanup is a PERIODIC tick (bucket-b). Its sweep is idempotent (acts on
            // TTL/expiry state via a fresh scope), so the scheduled-tick handler is registered in
            // BOTH modes; the in-process timer is hosted only under TriggerMode=Poll (default,
            // on-prem), keeping that path byte-for-byte unchanged.
            services.TryAddSingleton<WorkspaceCleanupService>();
            services.AddSingleton<IScheduledTickHandler, WorkspaceCleanupScheduledTickHandler>();
            if (ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration))
            {
                services.AddHostedService(sp => sp.GetRequiredService<WorkspaceCleanupService>());
            }
        }

        // Built-in process catalog (ticket #735)
        services.TryAddSingleton<IProcessCatalog, BuiltInProcessCatalog>();

        // Exposes the canonical plan validator's presence-based (including conditional)
        // input requirements so translation/migration tooling cannot certify a mapping
        // the submit path would reject (#2145).
        services.TryAddSingleton<IProcessConditionalInputProbe, ProcessConditionalInputProbe>();

        // Usage-ranked GP tool tiering telemetry (#2144). Real, queryable,
        // process-local store the dispatcher records into and an admin view ranks.
        services.TryAddSingleton<IProcessUsageTelemetry, InMemoryProcessUsageTelemetry>();

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
                    sp.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
                    sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));
        }

        // Execution admission controls (ticket #739) — rate, concurrency, cost, backpressure
        services
            .AddOptions<ExecutionAdmissionOptions>()
            .Bind(configuration.GetSection(ExecutionAdmissionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IExecutionAdmissionEvaluator, ExecutionAdmissionEvaluator>();

        // Cohesive sub-services the shared job service delegates to (authorization/approval,
        // admission+queue+workload+backend dispatch, the custom-code submit-token gate, and
        // raster-input/result-package artifacts). Registered as singletons so the production
        // GeoprocessingJobService constructor binds them; their own optional collaborators
        // resolve to null/empty when the backing infrastructure is absent.
        services.TryAddSingleton<GeoprocessingJobAuthorizer>();
        // Submit-time per-layer read gate (#2283 review). Prefers the ambient request to
        // reach the shared access pipeline (grants, tenant scope, metadata snapshot), so
        // the accessor is registered here rather than assumed from the host wiring; the
        // IServiceScopeFactory the container always provides covers contextless
        // (workflow/background) submissions, which are evaluated against the submitting
        // principal over a fresh scope rather than denied outright (#3043 review).
        services.AddHttpContextAccessor();
        services.TryAddSingleton<GeoprocessingLayerAccessGuard>();
        services.TryAddSingleton<GeoprocessingJobDispatcher>();
        services.TryAddSingleton<CustomCodeJobSubmissionGate>();
        services.TryAddSingleton<GeoprocessingJobArtifactService>();

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
        services
            .AddOptions<RasterOutputPublicationOptions>()
            .Bind(configuration.GetSection(RasterOutputPublicationOptions.SectionName))
            .Validate(
                options => RasterOutputWorkerContract.IsLogicalStoreReference(options.StoreReference),
                "Raster output StoreReference must be a bounded logical identifier.")
            .Validate(
                options => RasterOutputWorkerContract.IsLogicalStoreReference(options.RegistrationTarget),
                "Raster output RegistrationTarget must be a bounded logical identifier.")
            .ValidateOnStart();

        // Built-in production executors (ticket #1031; GP Devkit authoring
        // contract #2122). Each per-process executor implements IProcessExecutor
        // and self-declares the process id(s) it handles, so they are registered
        // through a single auto-registration scan rather than ~31 hand-maintained
        // lines, and GeoprocessingDispatchJobExecutor — the single IJobExecutor
        // registered for ExecutionJobKind.Geoprocessing — builds its routing table
        // by enumerating IProcessExecutor and keying by ProcessIds.
        //
        // The executor families composed behind the dispatcher:
        //  - geometry.* deterministic single-feature / aggregate vector ops
        //    (buffer/clip/intersect/project/area/union/centroid/length/
        //     convex-hull/dissolve/simplify/snap), plus the catalog-honesty
        //    reconciliation of geometry.make-valid + geometry.difference (#1185);
        //  - analytics.*-managed NTS counterparts to the PostGIS-protocol analytics
        //    paths (spatial-join/cluster/buffer-aggregate/density, #1260);
        //  - transform.* / source.* / sink.* GeoETL executors (feat/geoetl-baseline
        //    reconciled onto the #1185 add-a-capability contract), including the
        //    relational attribute-join / aggregate / pivot / unpivot transforms and
        //    transform.computed-field op=expression (feat/etl-expression-and-joins);
        //  - import.dataset durable import orchestration (#1630).
        AddProcessExecutors(services);

        // Imagery/ML inference delegation (#2241): imagery.classify submits the
        // source raster + a model reference to a configured cloud endpoint and
        // lands the result as a GP artifact. DELIBERATELY DORMANT when
        // unconfigured — no startup validation, no eager probe; an unconfigured
        // deployment fails the job at execution with a clear message. Provider
        // adapters sit behind IImageryInferenceClient; the generic 'http' REST
        // adapter, which speaks Honua's own JSON inference contract (implemented
        // by a model server directly or by a thin gateway in front of one), is the
        // supported backend in this build.
        services
            .AddOptions<Inference.ImageryInferenceOptions>()
            .Bind(configuration.GetSection(Inference.ImageryInferenceOptions.SectionName));
        // Timeout.InfiniteTimeSpan is deliberate: HttpClient's 100s default would
        // cancel long-running inference well before the configured TimeoutSeconds
        // (default 300, up to 3600) and the adapter would then misreport that the
        // configured duration elapsed. The adapter's linked per-request
        // CancellationTokenSource is the single authoritative deadline.
        services
            .AddHttpClient(Inference.HttpImageryInferenceClient.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            // AllowAutoRedirect = false is a SECURITY control, not a preference: a
            // 307/308 from an https endpoint preserves the POST body, so following
            // it to an http destination would resend the API key and the entire
            // source raster in plaintext, sailing straight past the one-time
            // scheme check the adapter performs on the configured endpoint.
            .ConfigurePrimaryHttpMessageHandler(
                () => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Inference.IImageryInferenceClient, Inference.HttpImageryInferenceClient>());
        Register<ImageryInferenceJobExecutor>(services);

        // First-class remote DAG source connectors (source.honua-layer,
        // source.esri-featureserver, source.ogc-features, source.wfs, source.postgis).
        // One RemoteSourceExecutor is registered per source process id; each resolves
        // its matching IDagFeatureSource (which reuses the existing import readers'
        // pagination/streaming) at execution time through an IServiceScopeFactory
        // scope, mirroring the ImportDatasetJobExecutor / ExternalPostgisSinkExecutor
        // provider-resolution pattern. The dispatcher routes by HandledProcessId.
        // Note: no manual "capturedProcessId" copy is needed here — each foreach
        // iteration variable has its own scope since C# 5, so the lambda below already
        // closes over a distinct sourceProcessId per registration.
        foreach (var sourceProcessId in RemoteSourceProcessIds)
        {
            // Surface each per-source executor into the IProcessExecutor enumerable the
            // dispatcher's route-table scan consumes, so remote sources route through the
            // same single auto-registration path as every other per-process executor.
            services.AddSingleton<IProcessExecutor>(sp => RemoteSourceExecutor.ForProcess(
                sourceProcessId,
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
                sp.GetRequiredService<ILogger<RemoteSourceExecutor>>()));
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, GeoprocessingDispatchJobExecutor>());

        // Headless single-process runner (GP Devkit, #2123): invokes one managed
        // IProcessExecutor directly with no Redis/queue/job-store, reusing the same
        // executor instances registered above. Consumed by the dev `honua gp` CLI.
        services.TryAddSingleton(sp =>
            new GeoprocessingLocalRunner(sp.GetServices<IProcessExecutor>()));

        // Custom-code geoprocessing (Phase 1). Bind the policy/config and register
        // the thin custom-code dispatch executor. The executor is fenced to the
        // "custom-code" runtime profile so neither the lean dispatcher above nor the
        // native GDAL worker can claim a custom-code job — and, if a custom-code job
        // is ever claimed in-process (a deployment that forgot to configure the
        // custom-code Batch workload), it fails closed rather than running untrusted
        // user code in the server process. The scoped-job token issuer that the
        // submit path and terminal callback consume is registered by
        // AddPortalTokenAuthentication in the Honua.Server composition root.
        services
            .AddOptions<CustomCodeOptions>()
            .Bind(configuration.GetSection(CustomCodeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Commit-signature verification seam for the signed-only repo-trust posture
        // (Phase 3 supply-chain hardening). The default is the fail-closed verifier:
        // a deployment that selects signed-only without registering a provider-backed
        // verifier rejects every submission rather than admitting unsigned code. A
        // real verifier (e.g. a GitHub commit-verification adapter) replaces this by
        // registering ICustomCodeCommitSignatureVerifier before this call.
        services.TryAddSingleton<ICustomCodeCommitSignatureVerifier>(
            UnverifiableCommitSignatureVerifier.Instance);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, CustomCodeDispatchJobExecutor>());

        // Job orchestration substrate (queue, log store — ticket #681) is wired
        // by the Honua.Server composition root via AddJobOrchestration, which
        // also registers the Share-export terminal callback that depends on
        // Honua.Server.Features.Admin.Share (out of reach for this assembly).

        return services;
    }

    /// <summary>
    /// Auto-registers every built-in <see cref="IProcessExecutor"/> (GP Devkit
    /// authoring contract #2122). Each executor is registered as its concrete type
    /// (so call sites that resolve a specific executor keep working) and is also
    /// surfaced into the enumerable <see cref="IProcessExecutor"/> set the
    /// <see cref="GeoprocessingDispatchJobExecutor"/> enumerates — both bound to the
    /// SAME singleton instance via a resolving factory, so there is exactly one of
    /// each. No reflection scan is used; combined with the
    /// <c>[DynamicallyAccessedMembers(PublicConstructors)]</c> annotation on
    /// <c>Register</c>'s type parameter (which roots each executor's constructor for
    /// the trimmer), the registration is Native-AOT-safe. Adding a new managed process
    /// is a one-line <c>Register&lt;T&gt;</c> call here plus its catalog entry.
    /// </summary>
    private static void AddProcessExecutors(IServiceCollection services)
    {
        Register<GeometryBufferJobExecutor>(services);
        Register<GeometryClipJobExecutor>(services);
        Register<GeometryIntersectJobExecutor>(services);
        Register<GeometryProjectJobExecutor>(services);
        Register<GeometryAreaJobExecutor>(services);
        Register<GeometryUnionJobExecutor>(services);
        Register<GeometryCentroidJobExecutor>(services);
        Register<GeometryLengthJobExecutor>(services);
        Register<GeometryConvexHullJobExecutor>(services);
        Register<GeometryDissolveJobExecutor>(services);
        Register<GeometrySimplifyJobExecutor>(services);
        Register<GeometrySnapJobExecutor>(services);
        Register<GeometryMakeValidJobExecutor>(services);
        Register<GeometryDifferenceJobExecutor>(services);
        Register<ManagedSpatialJoinExecutor>(services);
        Register<ManagedClusterExecutor>(services);
        Register<ManagedBufferAggregateExecutor>(services);
        Register<ManagedDensityExecutor>(services);
        // Spatial-statistics tool pack (#2142): Hot Spot Analysis (Getis-Ord Gi*).
        Register<ManagedHotSpotExecutor>(services);
        // Layer-aware, layer-SOURCED managed ops (#2322, #2325): the job-executable
        // counterparts of the layer-scoped PostGIS analytics/generalization/conversion
        // ops. Each streams a Honua catalog layer through source.honua-layer and runs a
        // managed NetTopologySuite op in one dispatched job, so the per-operation OGC
        // API - Processes projections (#1382) that advertise a layerId input reach a
        // terminal SUCCEEDED state instead of being refused at dispatch.
        Register<LayerBufferAggregateExecutor>(services);
        Register<LayerFeatureProjectExecutor>(services);
        Register<LayerDissolveExecutor>(services);
        Register<LayerSimplifyExecutor>(services);
        // Two-layer analytics.spatial-join (#2322): resolves BOTH the target layerId and
        // the joinLayerId through source.honua-layer and enriches each target feature
        // with matched join attributes/aggregates in one dispatched job.
        Register<LayerSpatialJoinExecutor>(services);
        // Async batch enrichment (#2283): the job-executable counterpart of
        // POST /api/enrich — resolves a managed enrichment dataset by id and joins
        // a layer-backed or staged inline target set against the dataset's layer
        // through the shared spatial-join computation.
        Register<EnrichmentJobExecutor>(services);
        // Layer-aware overlay tool pack (#2206, #2139).
        Register<OverlayClipExecutor>(services);
        Register<OverlayIntersectExecutor>(services);
        Register<OverlayUnionExecutor>(services);
        Register<OverlayEraseExecutor>(services);
        Register<OverlayMergeExecutor>(services);
        Register<OverlaySplitExecutor>(services);
        Register<DataManagementAppendExecutor>(services);
        // Proximity tool pack (#2139).
        Register<ProximityNearExecutor>(services);
        Register<ProximityNearTableExecutor>(services);
        // Statistics/summarization tool pack (#2140).
        Register<StatisticsSummarizeExecutor>(services);
        Register<StatisticsFrequencyExecutor>(services);
        Register<StatisticsCalculateExecutor>(services);
        Register<AttributeRenameTransformExecutor>(services);
        Register<AttributeCastTransformExecutor>(services);
        Register<ComputedFieldTransformExecutor>(services);
        Register<AttributeFilterTransformExecutor>(services);
        // Relational transforms (feat/etl-expression-and-joins): attribute-join /
        // aggregate / pivot / unpivot close the join/group-by/pivot gap in the DAG
        // transform set; transform.computed-field op=expression is backed by the AOT
        // expression engine inside ComputedFieldTransformExecutor (already registered).
        Register<AttributeJoinTransformExecutor>(services);
        Register<AggregateTransformExecutor>(services);
        Register<PivotTransformExecutor>(services);
        Register<UnpivotTransformExecutor>(services);
        Register<SpatialFilterTransformExecutor>(services);
        Register<ClipTransformExecutor>(services);
        Register<DedupTransformExecutor>(services);
        Register<ReprojectTransformExecutor>(services);
        Register<GeoJsonSourceExecutor>(services);
        Register<CsvSourceExecutor>(services);
        Register<GeoJsonFileSinkExecutor>(services);
        Register<QuarantineSinkExecutor>(services);
        Register<ExternalPostgisSinkExecutor>(services);
        // sink.honua-layer loads into a named catalog layer through the optional
        // IHonuaLayerSink capability. The executor is always registered so the catalog
        // advertises the node and can fail it closed with a clear message; the capability
        // it depends on is registered only when the catalog database is present (#2210).
        Register<HonuaLayerSinkExecutor>(services);
        Register<ImportDatasetJobExecutor>(services);
    }

    // [DynamicallyAccessedMembers] is REQUIRED for Native AOT: TryAddSingleton<T>()
    // annotates its type parameter with PublicConstructors so the trimmer roots the
    // constructor the DI container later reflects over. Without propagating that
    // annotation through this generic helper, ILC trims every executor's constructor
    // (the call sites pass concrete types, but the requirement does not flow), and the
    // AOT image then crash-loops at startup under ValidateOnBuild with ~490 "a suitable
    // constructor for type '...Executor' could not be located" errors — even though the
    // JIT image, which binds constructors at runtime, starts fine.
    private static void Register<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TExecutor>(
        IServiceCollection services)
        where TExecutor : class, IProcessExecutor
    {
        services.TryAddSingleton<TExecutor>();

        // Surface the same singleton into the IProcessExecutor enumerable the
        // dispatcher consumes. Guard against a double-add so a repeated
        // AddGeoprocessing call cannot register a duplicate id (which would trip
        // the route-table duplicate-id guard at composition time).
        if (!services.Any(d =>
                d.ServiceType == typeof(IProcessExecutor)
                && d.ImplementationFactory is not null
                && d.ImplementationFactory.Method.ReturnType == typeof(TExecutor)))
        {
            services.AddSingleton<IProcessExecutor>(sp => sp.GetRequiredService<TExecutor>());
        }
    }
}
