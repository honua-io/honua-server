// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.ControlPlane;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Honua.Worker.Gdal;

/// <summary>
/// Registers the heavyweight GDAL worker. The worker reuses the durable
/// job-execution substrate from <c>Honua.Server</c> (the same
/// <c>JobExecutionService</c> claim/lease/heartbeat loop, <c>RedisJobQueue</c>,
/// and Redis-backed stores) rather than forking a parallel runtime — it simply
/// registers a native-profile <see cref="IJobExecutor"/> set and lets the shared
/// loop dispatch to it. GDAL is reached through the <c>gdalwarp</c> / <c>ogr2ogr</c>
/// CLI tools shipped in the worker image base layer; no managed GDAL bindings or
/// native package references are introduced.
/// </summary>
public static class GdalWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Wires the GDAL worker host: Redis connection, the shared durable execution
    /// substrate (queue, job store, log store, cancellation registry), the two
    /// substrate hosted services (execution + reconciliation), and the
    /// native-profile GDAL executors.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Host configuration. Requires the <c>redis</c> connection string.</param>
    public static IServiceCollection AddGdalWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var redisConnectionString = configuration.GetConnectionString("redis")
            ?? configuration["ConnectionStrings:redis"]
            ?? throw new InvalidOperationException(
                "The GDAL worker requires a 'redis' connection string (the durable job substrate's "
                + "coordination layer per ADR-0031 / ADR-0038).");

        services.TryAddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redisConnectionString));

        // Shared durable substrate stores. These are the same internal Redis-backed
        // implementations the API/serving host registers; the worker host reuses them
        // so claim/lease/heartbeat/finalization semantics are identical.
        services.TryAddSingleton<IExecutionJobStore>(sp =>
            new RedisExecutionJobStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));

        services.TryAddSingleton<RedisJobQueue>(sp =>
            new RedisJobQueue(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IExecutionJobStore>(),
                sp.GetRequiredService<ILogger<RedisJobQueue>>()));
        services.TryAddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
        services.TryAddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());

        services.TryAddSingleton<IExecutionLogStore>(sp =>
            new RedisExecutionLogStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));

        // Worker-side execution loop dependencies and the two hosted services. This
        // is the SAME JobExecutionService / JobReconciliationService the substrate
        // ships; the worker does not introduce its own BackgroundService.
        services.AddGdalWorkerExecutionLoop();

        // GDAL executor options, CLI runner, and the native-profile executor set.
        // The per-process executors implement IProcessExecutor and self-declare their
        // process id(s) (GP Devkit authoring contract #2122), so GdalDispatchJobExecutor
        // builds its routing table by enumerating IProcessExecutor instead of a
        // hand-maintained ctor. Shared with the Redis-free AddGdalProcessExecutors seam.
        services.AddGdalProcessExecutors(configuration);

        // Register the native dispatcher as the single IJobExecutor for the
        // Geoprocessing kind in this host. It declares AcceptedRuntimeProfiles =
        // { "native" }, so JobExecutionService passes that profile set to the
        // queue claim filter and the worker only claims native-profile jobs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, GdalDispatchJobExecutor>());

        return services;
    }

    /// <summary>
    /// Registers ONLY the native GDAL <see cref="IProcessExecutor"/> set plus its
    /// dependencies — the <c>GdalWorkerOptions</c>, the <see cref="IGdalCommandRunner"/>
    /// CLI runner, and each per-process executor — with NO Redis connection, queue,
    /// job store, or hosted execution loop. This is the Redis-free seam the GP Devkit
    /// headless runner / <c>honua gp</c> dev CLI consume (issue #2123) so a developer
    /// can run a single native op directly without the durable substrate
    /// <see cref="AddGdalWorker"/> wires for the worker host.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Host configuration; binds the <c>GdalWorker</c> section.</param>
    public static IServiceCollection AddGdalProcessExecutors(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddGdalProcessExecutors(configuration, GdalProcessExecutorMode.InProcess);

    /// <summary>
    /// Registers the native GDAL <see cref="IProcessExecutor"/> set with an explicit
    /// command-runner mode (issue #2180).
    /// <list type="bullet">
    /// <item><see cref="GdalProcessExecutorMode.InProcess"/> — the default fast path:
    /// each GDAL tool is shelled out via <see cref="ProcessGdalCommandRunner"/> against
    /// the host's GDAL CLIs, keeping the GP Devkit sub-second loop on managed ops.</item>
    /// <item><see cref="GdalProcessExecutorMode.Container"/> — the opt-in
    /// <c>--real-worker</c> fidelity path: each GDAL tool runs inside the real
    /// <c>honua-worker-etl</c> image via <see cref="DockerGdalCommandRunner"/>, so the
    /// image / CRS data / driver-set / arg-handling boundary a production native submit
    /// crosses is actually exercised locally.</item>
    /// </list>
    /// Both modes register the IDENTICAL executor set and read the IDENTICAL durable
    /// spec — only the <see cref="IGdalCommandRunner"/> implementation differs, so the
    /// managed fast path is unaffected and the spec a job carries is the same.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Host configuration; binds the <c>GdalWorker</c> section.</param>
    /// <param name="mode">Which command-runner implementation to register.</param>
    public static IServiceCollection AddGdalProcessExecutors(
        this IServiceCollection services,
        IConfiguration configuration,
        GdalProcessExecutorMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GdalWorkerOptions>()
            .Bind(configuration.GetSection(GdalWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (mode == GdalProcessExecutorMode.Container)
        {
            // Container-exec fidelity path: bind the GdalContainer options and run each
            // GDAL tool inside the real worker image. The in-process runner is NOT
            // registered, so the same executor code path shells out via docker instead.
            services
                .AddOptions<GdalContainerExecutionOptions>()
                .Bind(configuration.GetSection("GdalContainer"));
            services.TryAddSingleton<IDockerCommandInvoker, ProcessDockerCommandInvoker>();
            services.TryAddSingleton<IGdalCommandRunner, DockerGdalCommandRunner>();
        }
        else
        {
            services.TryAddSingleton<IGdalCommandRunner, ProcessGdalCommandRunner>();
        }

        RegisterGdalExecutor<GdalVectorConvertJobExecutor>(services);
        RegisterGdalExecutor<GdalVectorReprojectJobExecutor>(services);
        RegisterGdalExecutor<GdalVectorSourceReadJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterReprojectJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterResampleJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterInterpolateJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterMosaicJobExecutor>(services);
        RegisterGdalExecutor<GdalSurfaceJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterClipJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterReprojectCatalogJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterFormatConvertJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterStatisticsJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterZonalStatisticsJobExecutor>(services);
        RegisterGdalExecutor<GdalMultidimCoverageMetadataJobExecutor>(services);
        RegisterGdalExecutor<PdalPointCloudConvertJobExecutor>(services);

        return services;
    }

    /// <summary>
    /// DEV-ONLY: decorates the registered native <see cref="IGdalCommandRunner"/> with the
    /// glass-box capturing runner so the supplied <see cref="GlassBoxCapture"/> collects the
    /// fully unsanitized command line, real scratch working directory, and complete
    /// stdout/stderr of every native invocation (GP Devkit, issue #2128).
    /// </summary>
    /// <remarks>
    /// This MUST be called only by the dev <c>honua gp</c> CLI under its explicit glass-box
    /// flag/env. The production worker host (<see cref="AddGdalWorker"/>) never calls it, so
    /// the sanitized <see cref="GdalErrorSanitizer"/> / <see cref="GdalCommandLog"/> path
    /// stays the default and no raw paths or untruncated output ever reach a client. Call it
    /// after <see cref="AddGdalProcessExecutors(IServiceCollection, IConfiguration)"/> so the bare runner is already registered.
    /// </remarks>
    /// <param name="services">Service collection that has the GDAL executors registered.</param>
    /// <param name="capture">The sink the decorator records unsanitized invocations into.</param>
    public static IServiceCollection AddGlassBoxGdalCapture(
        this IServiceCollection services,
        GlassBoxCapture capture)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(capture);

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IGdalCommandRunner))
            ?? throw new InvalidOperationException(
                "AddGlassBoxGdalCapture requires AddGdalProcessExecutors to have registered an IGdalCommandRunner first.");

        services.Remove(inner);
        services.AddSingleton(capture);
        services.AddSingleton<IGdalCommandRunner>(sp =>
            new GlassBoxGdalCommandRunner(MaterializeInner(sp, inner), capture));

        return services;
    }

    private static IGdalCommandRunner MaterializeInner(IServiceProvider sp, ServiceDescriptor inner)
    {
        if (inner.ImplementationInstance is IGdalCommandRunner instance)
        {
            return instance;
        }

        if (inner.ImplementationFactory is not null)
        {
            return (IGdalCommandRunner)inner.ImplementationFactory(sp);
        }

        var implementationType = inner.ImplementationType
            ?? throw new InvalidOperationException("The registered IGdalCommandRunner has no resolvable implementation.");
        return (IGdalCommandRunner)ActivatorUtilities.CreateInstance(sp, implementationType);
    }

    /// <summary>
    /// Registers the shared durable execution-loop services used by the worker host.
    /// Intentionally does NOT register the serving image's
    /// <c>GeoprocessingJobTerminalCallback</c> (an API-side result-projection
    /// concern); the worker leaves the terminal-callback set empty, which the loop
    /// supports.
    /// </summary>
    private static IServiceCollection AddGdalWorkerExecutionLoop(this IServiceCollection services)
    {
        services.TryAddSingleton<ExecutionJobCancellationTokens>();
        services.TryAddSingleton<IJobCancellationNotifier>(
            sp => sp.GetRequiredService<ExecutionJobCancellationTokens>());

        services.AddHostedService<JobExecutionService>();
        services.AddHostedService<JobReconciliationService>();

        return services;
    }

    /// <summary>
    /// Registers a GDAL <see cref="IProcessExecutor"/> as its concrete type and
    /// surfaces the same singleton into the enumerable <see cref="IProcessExecutor"/>
    /// set <see cref="GdalDispatchJobExecutor"/> consumes — both bound to one
    /// instance via a resolving factory (GP Devkit authoring contract #2122).
    /// </summary>
    private static void RegisterGdalExecutor<TExecutor>(IServiceCollection services)
        where TExecutor : class, IProcessExecutor
    {
        services.TryAddSingleton<TExecutor>();

        if (!services.Any(d =>
                d.ServiceType == typeof(IProcessExecutor)
                && d.ImplementationFactory is not null
                && d.ImplementationFactory.Method.ReturnType == typeof(TExecutor)))
        {
            services.AddSingleton<IProcessExecutor>(sp => sp.GetRequiredService<TExecutor>());
        }
    }
}
