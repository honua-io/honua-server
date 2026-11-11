// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Io.Export;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.Infrastructure.Abstractions;
using Honua.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Startup;

internal static class ImportExportTileOperationsRegistration
{
    public static IServiceCollection AddHonuaImportExportAndTileOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Universal progress store backs every import/export/tile-operation job manager below.
        // A host with no system database (a unit-level host) keeps progress in its own memory.
        services.AddPostgresSharedState(configuration);
        services.AddSingleton<IUniversalProgressStore>(sp =>
            sp.GetService<Npgsql.NpgsqlDataSource>() is not null
                ? sp.GetRequiredService<Honua.Server.Features.Infrastructure.Progress.PostgresUniversalProgressStore>()
                : new UniversalProgressStore(sp.GetService<IDistributedCache>(), sp.GetRequiredService<ILogger<UniversalProgressStore>>()));

        services.Configure<FileUploadOptions>(
            configuration.GetSection(FileUploadOptions.SectionName));
        services.Configure<MigrationUrlValidationOptions>(
            configuration.GetSection(MigrationUrlValidationOptions.SectionName));
        services.AddSingleton<StreamingFileUploadService>();
        services.AddSingleton<IUploadQueueMetricsProvider>(sp =>
            sp.GetRequiredService<StreamingFileUploadService>());

        services.AddSingleton<IDistributedImportJobManager>(sp =>
            new RedisImportJobManager(
                sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetService<IDistributedCache>(),
                sp.GetRequiredService<ILogger<RedisImportJobManager>>(),
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetService<IConnectionMultiplexer>()));
        // Event-handler Lambdas (pool cap 2, frozen between invocations) do not poll.
        // The API Lambda still does. honua-server#6515.
        var hostPollingLoops = ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration);
        if (hostPollingLoops)
        {
            services.AddHostedService<GeoservicesImportBackgroundService>();
        }

        // Footprint-driven batch import orchestration (#1253). The orchestrator
        // composes the per-layer Geoservices import pipeline into an ordered,
        // resumable batch run; the background service advances active batches
        // under the shared import leader election.
        services.AddScoped<IMigrationBatchOrchestrator, MigrationBatchOrchestrator>();
        if (hostPollingLoops)
        {
            services.AddHostedService<MigrationBatchBackgroundService>();
        }
        services.AddSingleton<GeoServerImportJobManager>(sp =>
            new GeoServerImportJobManager(
                sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetService<IDistributedCache>(),
                sp.GetRequiredService<ILogger<GeoServerImportJobManager>>(),
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetService<IConnectionMultiplexer>()));
        if (hostPollingLoops)
        {
            services.AddHostedService<GeoServerImportBackgroundService>();
        }

        // Migration performance evidence store (#1033 slice 5).
        // Surface the source-generated JsonTypeInfo so the Postgres store can round-trip the artifact
        // JSON without reflection. The in-memory store is registered as the fallback so dev / test
        // profiles without a Postgres infrastructure still serve the admin/SDK endpoints.
        services.AddSingleton<JsonTypeInfo<MigrationPerformanceEvidenceArtifact>>(
            ImportJsonContext.Default.MigrationPerformanceEvidenceArtifact);
        services.AddSingleton<InMemoryMigrationPerformanceEvidenceStore>();
        services.TryAddScoped<IMigrationPerformanceEvidenceStore>(sp =>
            sp.GetRequiredService<InMemoryMigrationPerformanceEvidenceStore>());

        // Migration-run checkpoint store (#2459, ADR-0060). Durable, cross-request resume
        // state must not live on a compute node's local disk. The in-memory store is the
        // default so stores-less dev/test profiles still resolve the dependency; it is a
        // process-wide singleton (a per-scope instance would silently drop checkpoints
        // between requests). The Postgres registration (AddPostgreSqlServices, which runs
        // earlier) overrides it via TryAdd with the durable shared-store implementation.
        services.AddSingleton<InMemoryMigrationRunCheckpointStore>();
        services.TryAddScoped<IMigrationRunCheckpointStore>(sp =>
            sp.GetRequiredService<InMemoryMigrationRunCheckpointStore>());

        // Exports above the synchronous threshold run as Export execution jobs.
        services.AddSingleton<IExportJobService>(sp => new ExportJobService(
            sp.GetRequiredService<IUniversalProgressStore>(),
            sp.GetRequiredService<ILogger<ExportJobService>>(),
            sp.GetService<IExecutionJobStore>(),
            sp.GetService<IJobQueue>()));
        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IJobExecutor, ExportJobExecutor>());
        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IJobTerminalCallback, ExportJobTerminalCallback>());

        // Resumable generated tile-cache seed/warm generation checkpoint store (#2661). Durable
        // cross-request resume state must not live only on a serving pod's memory. The in-memory
        // store is the default so stores-less dev/test profiles still resolve the dependency; when
        // a Redis multiplexer is present it is overridden with the durable cross-node store so a
        // fix-forward retry resumes the generation instead of restarting the whole grid. The Redis
        // binding is registered first so the TryAdd fallback below does not replace it.
        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.AddSingleton<Honua.Core.Features.Tiles.ITileCacheGenerationCheckpointStore>(sp =>
                new RedisTileCacheGenerationCheckpointStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisTileCacheGenerationCheckpointStore>>()));
        }

        services.AddSingleton<Honua.Core.Features.Tiles.InMemoryTileCacheGenerationCheckpointStore>();
        services.TryAddSingleton<Honua.Core.Features.Tiles.ITileCacheGenerationCheckpointStore>(sp =>
            sp.GetRequiredService<Honua.Core.Features.Tiles.InMemoryTileCacheGenerationCheckpointStore>());

        // Tile operations run as TileCache execution jobs: the record and its queue position are
        // rows in the system database, so an accepted operation runs once on whichever node claims it.
        services.Configure<TileCacheBatchOptions>(
            configuration.GetSection(TileCacheBatchOptions.SectionName));
        services.TryAddSingleton<ITileCacheJobService>(sp => new TileCacheJobService(
            sp.GetService<IExecutionJobStore>(),
            sp.GetRequiredService<IUniversalProgressStore>(),
            sp.GetServices<IBatchComputeBackend>(),
            sp.GetRequiredService<IOptionsMonitor<TileCacheBatchOptions>>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TileCacheJobService>>(),
            sp.GetService<IJobQueue>()));
        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IJobExecutor, TileCacheJobExecutor>());
        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IJobTerminalCallback, TileCacheJobTerminalCallback>());
        services.Configure<TileCacheWarmingOptions>(
            configuration.GetSection(TileCacheWarmingOptions.SectionName));
        if (hostPollingLoops)
        {
            services.AddHostedService<TileCacheWarmingHostedService>();
        }

        // Scheduled tile-cache expiry/invalidation (#1837): the time-based complement to the
        // per-tileset Cache-Control TTL (#1794). Disabled unless TileCacheExpiry:Enabled is set.
        services.Configure<TileCacheExpiryOptions>(
            configuration.GetSection(TileCacheExpiryOptions.SectionName));

        // Scheduled tile-cache expiry is a PERIODIC tick (bucket-b). The sweep re-queues invalidate
        // jobs whose own pipeline is idempotent, so the scheduled-tick handler is registered in BOTH
        // trigger modes; the in-process timer is hosted only under TriggerMode=Poll (default,
        // on-prem), keeping that path byte-for-byte unchanged.
        services.TryAddSingleton<TileCacheExpiryHostedService>();
        services.AddSingleton<IScheduledTickHandler, TileCacheExpiryScheduledTickHandler>();
        if (ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration))
        {
            services.AddHostedService(sp => sp.GetRequiredService<TileCacheExpiryHostedService>());
        }

        // The live Redis tile-key index backs both optional quota eviction (#1917) and operator
        // expire/delete lifecycle operations (#2661). Register it whenever Redis is present so
        // lifecycle correctness does not depend on the independent eviction opt-in. The eviction
        // service still checks TileOptions:Eviction:Enabled before sweeping.
        var redisConfigured = services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer));
        if (redisConfigured)
        {
            services.AddSingleton<Honua.Core.Features.Tiles.ITileCacheKeyIndex>(sp =>
                new RedisTileCacheKeyIndex(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisTileCacheKeyIndex>>(),
                    sp.GetService<ICloudFileStorage>()));
        }
        else
        {
            services.AddSingleton<Honua.Core.Features.Tiles.ITileCacheKeyIndex>(
                Honua.Core.Features.Tiles.NullTileCacheKeyIndex.Instance);
        }

        services.Configure<TileCacheEvictionSweepOptions>(
            configuration.GetSection(TileCacheEvictionSweepOptions.SectionName));
        services.AddSingleton<TileCacheEvictionService>();

        // Live LRU tile-cache eviction is a PERIODIC tick (bucket-b). The sweep re-evaluates the
        // current key index against the quota (no-op when nothing exceeds it), so the scheduled-tick
        // handler is registered in BOTH trigger modes; the in-process timer is hosted only under
        // TriggerMode=Poll (default, on-prem), keeping that path byte-for-byte unchanged. The handler
        // routes to TileCacheEvictionService.SweepAsync (the same body the timer calls).
        services.AddSingleton<IScheduledTickHandler, TileCacheEvictionScheduledTickHandler>();
        if (ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration))
        {
            services.AddHostedService<TileCacheEvictionHostedService>();
        }

        Honua.Server.Features.Import.DurableImportServiceCollectionExtensions.AddDurableBackgroundImports(services);
        Honua.Server.Features.Admin.LayerPublishServiceCollectionExtensions.AddLayerPublishJobs(services);
        return services;
    }
}
