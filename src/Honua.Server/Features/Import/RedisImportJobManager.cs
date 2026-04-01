// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Server.Features.Import;

/// <summary>
/// Redis-based distributed import job manager with in-memory fallback.
/// Uses StackExchange.Redis primitives when available, falling back to IDistributedCache.
/// </summary>
internal sealed partial class RedisImportJobManager : IDistributedImportJobManager, IImportCoordinationHealth, IDisposable
{
    private readonly RedisJobQueue _jobQueue;
    private readonly RedisLeaderElection _leaderElection;
    private readonly IUniversalProgressStore _universalProgressStore;
    private readonly IDistributedProgressStore<GeoservicesImportProgress> _progressStore;
    private readonly RedisProgressStore<GeoservicesImportRequest> _requestStore;
    private readonly bool _requiresStrictDistributedMode;

    public RedisImportJobManager(
        IUniversalProgressStore universalProgressStore,
        IDistributedCache? distributedCache,
        ILogger<RedisImportJobManager> logger,
        IHostEnvironment hostEnvironment,
        IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(universalProgressStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";

        _requiresStrictDistributedMode = DistributedCoordinationMode.RequiresStrictDistributedMode(hostEnvironment);
        _jobQueue = new RedisJobQueue(redis, logger, "geoservices:import:queue", allowFallback: !_requiresStrictDistributedMode);
        _leaderElection = new RedisLeaderElection(redis, logger, "geoservices:import:leader", instanceId);
        _universalProgressStore = universalProgressStore;
        _progressStore = new DistributedProgressStoreAdapter<GeoservicesImportProgress>(universalProgressStore);
        _requestStore = new RedisProgressStore<GeoservicesImportRequest>(
            distributedCache,
            logger,
            "geoservices:import:request:",
            GeoservicesImportJsonContext.Default.GeoservicesImportRequest,
            redis);
    }

    public IDistributedJobQueueService JobQueue => _jobQueue;
    public IDistributedLeaderElection LeaderElection => _leaderElection;
    public IDistributedProgressStore<GeoservicesImportProgress> ProgressStore => _progressStore;
    public IDistributedProgressStore<GeoservicesImportRequest> RequestStore => _requestStore;

    internal bool IsClusterDurable =>
        !_jobQueue.IsUsingFallback &&
        !_leaderElection.IsUsingFallback &&
        !_requestStore.IsUsingFallback &&
        _universalProgressStore is not UniversalProgressStore { IsUsingFallback: true };

    public bool CanAcceptNewJobs => IsClusterDurable || !_requiresStrictDistributedMode;

    public void Dispose()
    {
        _leaderElection.Dispose();
    }
}

/// <summary>
/// JSON serialization context for Geoservices import types.
/// </summary>
[JsonSerializable(typeof(GeoservicesImportProgress))]
[JsonSerializable(typeof(GeoservicesImportRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GeoservicesImportJsonContext : JsonSerializerContext
{
}
