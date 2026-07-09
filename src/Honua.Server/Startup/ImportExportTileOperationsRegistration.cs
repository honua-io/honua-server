// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
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

/// <summary>
/// Registers import job managers, the export job pipeline, the migration-evidence store, and
/// the tile-operations job service (cache warming + reseed). These all share a common
/// progress-store + distributed-cache backbone, so they live together in one registration.
/// </summary>
internal static class ImportExportTileOperationsRegistration
{
    public static IServiceCollection AddHonuaImportExportAndTileOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Universal progress store backs every import/export/tile-operation job manager below.
        services.AddSingleton<IUniversalProgressStore>(sp =>
            new UniversalProgressStore(
                sp.GetService<IDistributedCache>(),
                sp.GetRequiredService<ILogger<UniversalProgressStore>>(),
                sp.GetService<IConnectionMultiplexer>()));

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
        services.AddHostedService<GeoservicesImportBackgroundService>();

        // Footprint-driven batch import orchestration (#1253). The orchestrator
        // composes the per-layer Geoservices import pipeline into an ordered,
        // resumable batch run; the background service advances active batches
        // under the shared import leader election.
        services.AddScoped<IMigrationBatchOrchestrator, MigrationBatchOrchestrator>();
        services.AddHostedService<MigrationBatchBackgroundService>();
        services.AddSingleton<GeoServerImportJobManager>(sp =>
            new GeoServerImportJobManager(
                sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetService<IDistributedCache>(),
                sp.GetRequiredService<ILogger<GeoServerImportJobManager>>(),
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetService<IConnectionMultiplexer>()));
        services.AddHostedService<GeoServerImportBackgroundService>();

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

        // Export background service with durable request persistence and a bounded scheduler.
        services.AddSingleton(System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(4)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
            }));
        services.AddSingleton<IExportJobService>(sp =>
            new ExportJobService(
                sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetService<IDistributedCache>(),
                sp.GetRequiredService<System.Threading.Channels.Channel<string>>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<ExportJobService>>(),
                sp.GetService<IConnectionMultiplexer>()));
        services.AddHostedService<ExportBackgroundService>();

        // Tile operations (cache warming + reseed + invalidation orchestration).
        services.AddSingleton<ITileOperationJobService>(sp =>
            new TileOperationJobService(
                sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetService<IDistributedCache>(),
                sp.GetRequiredService<OutputCacheInvalidationService>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<Honua.Core.Features.Tiles.TileOptions>>(),
                sp.GetRequiredService<IOptions<LimitsOptions>>(),
                sp.GetRequiredService<ILogger<TileOperationJobService>>(),
                sp.GetService<IConnectionMultiplexer>()));
        services.Configure<TileCacheWarmingOptions>(
            configuration.GetSection(TileCacheWarmingOptions.SectionName));
        services.AddHostedService<TileCacheWarmingHostedService>();
        services.AddHostedService<TileOperationBackgroundService>();

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

        // Live size-quota / LRU tile-cache evictor (#1917). The pure LRU policy and quota options
        // landed in #1837; this binds them to a live Redis tile-key index on the hot serve path.
        // The Redis-backed index is registered only when both eviction is enabled (so the baseline
        // serve path stays untouched) and a Redis multiplexer is present; otherwise a no-op index
        // keeps the hot path allocation-free. The evictor service + scheduled sweep are always
        // registered but no-op when the resolved index is disabled.
        var evictionEnabled = configuration
            .GetSection(Honua.Core.Features.Tiles.TileOptions.SectionName)
            .GetSection("Eviction")
            .GetValue<bool>("Enabled");
        var redisConfigured = services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer));
        if (evictionEnabled && redisConfigured)
        {
            services.AddSingleton<Honua.Core.Features.Tiles.ITileCacheKeyIndex>(sp =>
                new RedisTileCacheKeyIndex(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisTileCacheKeyIndex>>()));
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

        // Batch-dispatched tile-cache / PMTiles execution jobs (issue #1697).
        // The worker-side executor is registered unconditionally so any worker host
        // can claim ExecutionJobKind.TileCache jobs; it is only invoked when the
        // execution-job substrate routes a tile-cache job to it. The submission
        // service and its config are bound here too — the admin endpoint consults
        // ITileCacheJobService only when TileOperations:Batch:Enabled is set,
        // keeping the in-process channel as the zero-config default.
        services.Configure<TileCacheBatchOptions>(
            configuration.GetSection(TileCacheBatchOptions.SectionName));
        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<
                Honua.Core.Features.ControlPlane.Abstractions.IJobExecutor,
                TileCacheJobExecutor>());

        // The submission service depends on the durable execution-job store/queue,
        // which are only present when Redis is configured (mirrors AddGeoprocessing).
        // Gating registration on Redis keeps GetService<ITileCacheJobService>() from
        // throwing on a missing dependency in stores-less dev/test profiles; in those
        // profiles the admin endpoint simply uses the in-process channel path.
        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<ITileCacheJobService, TileCacheJobService>();
        }

        return services;
    }
}
