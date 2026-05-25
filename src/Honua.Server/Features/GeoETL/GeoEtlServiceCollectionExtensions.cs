// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Services;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.GeoETL;

/// <summary>
/// Registers the GeoETL baseline vertical slice: pipeline stores, the Phase 1
/// managed connectors and transforms, the component factory and validator, the stage
/// runner, and the <see cref="PipelineJobExecutor"/> the substrate's
/// <c>JobExecutionService</c> discovers for <c>ExtractTransformLoad</c> jobs.
/// </summary>
internal static class GeoEtlServiceCollectionExtensions
{
    /// <summary>
    /// Registers GeoETL services on the serving image (managed profile only). The
    /// Honua-layer sink's <see cref="IFeatureSinkWriter"/> is supplied by the storage
    /// provider (Honua.Postgres); when absent the sink is not registered and pipelines
    /// that target it fail validation, while the dry-run null sink still runs.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddGeoEtl(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Definition / execution stores. In-memory baseline; the durable
        // PostgreSQL-backed stores are Child Ticket A's remaining EF/DbUp work.
        services.TryAddSingleton<IPipelineDefinitionStore, InMemoryPipelineDefinitionStore>();
        services.TryAddSingleton<IPipelineExecutionStore, InMemoryPipelineExecutionStore>();

        // Phase 1 managed source connectors.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineSourceConnector, GeoJsonSourceConnector>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineSourceConnector, CsvSourceConnector>());

        // Phase 1 managed transforms.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, ReprojectTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, AttributeFilterTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, AttributeRenameTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, AttributeCastTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, ComputedFieldTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, SpatialFilterTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, ClipTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, SpatialJoinTransform>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, DedupTransform>());
        // Per-feature geometry operators (buffer / simplify / convex-hull /
        // centroid / make-valid / difference) lifted to layer scope. Reused by
        // the layer-scope geoprocessing executors so a GP process over a layer
        // runs as a single-transform pipeline rather than a single-WKB primitive.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineTransform, GeometryOperationTransform>());

        // Phase 1 sinks. The dry-run null preview sink is always available; the
        // Honua-layer / PostGIS sink registers only when a feature sink writer exists.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineSinkConnector, NullPreviewSinkConnector>());
        // Managed file + dead-letter sinks (no native deps, always available).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineSinkConnector, GeoJsonFileSinkConnector>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineSinkConnector, QuarantineSinkConnector>());
        if (services.Any(d => d.ServiceType == typeof(IFeatureSinkWriter)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IPipelineSinkConnector>(
                sp => new HonuaLayerSinkConnector(sp.GetRequiredService<IFeatureSinkWriter>())));
        }

        // Factory + validator + stage runner.
        services.TryAddScoped<IPipelineComponentFactory>(sp => new PipelineComponentFactory(
            sp.GetServices<IPipelineSourceConnector>(),
            sp.GetServices<IPipelineTransform>(),
            sp.GetServices<IPipelineSinkConnector>()));
        services.TryAddScoped<PipelineValidator>();
        services.TryAddScoped<PipelineStageRunner>();

        // Submission launcher (execution-submission path through the substrate).
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddScoped<PipelineExecutionLauncher>();

        // The single managed-profile executor for ExtractTransformLoad jobs. The
        // substrate's JobExecutionService is a singleton that builds its executor map
        // once, so the executor is a singleton that opens a DI scope per job to resolve
        // scoped stage components.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, PipelineJobExecutor>());

        return services;
    }
}
