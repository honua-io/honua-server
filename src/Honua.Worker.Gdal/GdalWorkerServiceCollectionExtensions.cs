// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.ControlPlane;
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

        // GDAL executor options.
        services
            .AddOptions<GdalWorkerOptions>()
            .Bind(configuration.GetSection(GdalWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // GDAL CLI runner + native-profile executors.
        services.TryAddSingleton<IGdalCommandRunner, ProcessGdalCommandRunner>();
        services.TryAddSingleton<GdalVectorConvertJobExecutor>();
        services.TryAddSingleton<GdalRasterReprojectJobExecutor>();

        // Register the native dispatcher as the single IJobExecutor for the
        // Geoprocessing kind in this host. It declares AcceptedRuntimeProfiles =
        // { "native" }, so JobExecutionService passes that profile set to the
        // queue claim filter and the worker only claims native-profile jobs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, GdalDispatchJobExecutor>());

        return services;
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
}
