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

        return services;
    }
}
