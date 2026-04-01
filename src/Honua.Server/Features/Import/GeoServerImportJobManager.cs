// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Server.Features.Import;

/// <summary>
/// Redis-backed coordination for GeoServer import jobs with in-memory fallback in development and tests.
/// </summary>
internal sealed class GeoServerImportJobManager : IImportCoordinationHealth, IDisposable
{
    private readonly RedisJobQueue _jobQueue;
    private readonly RedisLeaderElection _leaderElection;
    private readonly IUniversalProgressStore _universalProgressStore;
    private readonly IDistributedProgressStore<GeoServerImportProgress> _progressStore;
    private readonly RedisProgressStore<GeoServerImportRequest> _requestStore;
    private readonly bool _requiresStrictDistributedMode;

    public GeoServerImportJobManager(
        IUniversalProgressStore universalProgressStore,
        IDistributedCache? distributedCache,
        ILogger<GeoServerImportJobManager> logger,
        IHostEnvironment hostEnvironment,
        IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(universalProgressStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";

        _requiresStrictDistributedMode = DistributedCoordinationMode.RequiresStrictDistributedMode(hostEnvironment);
        _jobQueue = new RedisJobQueue(redis, logger, "geoserver:import:queue", allowFallback: !_requiresStrictDistributedMode);
        _leaderElection = new RedisLeaderElection(redis, logger, "geoserver:import:leader", instanceId);
        _universalProgressStore = universalProgressStore;
        _progressStore = new DistributedProgressStoreAdapter<GeoServerImportProgress>(universalProgressStore);
        _requestStore = new RedisProgressStore<GeoServerImportRequest>(
            distributedCache,
            logger,
            "geoserver:import:request:",
            GeoServerImportApiJsonContext.Default.GeoServerImportRequest,
            redis);
    }

    public bool CanAcceptNewJobs => IsClusterDurable || !_requiresStrictDistributedMode;

    internal IDistributedJobQueueService JobQueue => _jobQueue;
    internal IDistributedLeaderElection LeaderElection => _leaderElection;
    internal IDistributedProgressStore<GeoServerImportProgress> ProgressStore => _progressStore;
    internal IDistributedProgressStore<GeoServerImportRequest> RequestStore => _requestStore;
    internal bool IsClusterDurable =>
        !_jobQueue.IsUsingFallback &&
        !_leaderElection.IsUsingFallback &&
        !_requestStore.IsUsingFallback &&
        _universalProgressStore is not UniversalProgressStore { IsUsingFallback: true };

    public void Dispose()
    {
        _leaderElection.Dispose();
    }
}
