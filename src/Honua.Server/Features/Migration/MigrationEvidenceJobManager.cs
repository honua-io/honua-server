// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Server.Features.Import;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Server.Features.Migration;

/// <summary>
/// Redis-backed coordination for migration evidence jobs with in-memory fallback in development and tests.
/// </summary>
internal sealed class MigrationEvidenceJobManager : IImportCoordinationHealth, IDisposable
{
    private readonly RedisJobQueue _jobQueue;
    private readonly RedisLeaderElection _leaderElection;
    private readonly IUniversalProgressStore _universalProgressStore;
    private readonly IDistributedProgressStore<MigrationEvidenceProgress> _progressStore;
    private readonly RedisProgressStore<MigrationEvidenceRequest> _requestStore;
    private readonly bool _requiresStrictDistributedMode;

    public MigrationEvidenceJobManager(
        IUniversalProgressStore universalProgressStore,
        IDistributedCache? distributedCache,
        ILogger<MigrationEvidenceJobManager> logger,
        IHostEnvironment hostEnvironment,
        IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(universalProgressStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
        _requiresStrictDistributedMode = RedisImportJobManager.RequiresStrictDistributedMode(hostEnvironment);
        _jobQueue = new RedisJobQueue(redis, logger, "migration:evidence:queue", allowFallback: !_requiresStrictDistributedMode);
        _leaderElection = new RedisLeaderElection(redis, logger, "migration:evidence:leader", instanceId);
        _universalProgressStore = universalProgressStore;
        _progressStore = new DistributedProgressStoreAdapter<MigrationEvidenceProgress>(universalProgressStore);
        _requestStore = new RedisProgressStore<MigrationEvidenceRequest>(
            distributedCache,
            logger,
            "migration:evidence:request:",
            MigrationEvidenceJobJsonContext.Default.MigrationEvidenceRequest,
            redis);
    }

    public bool CanAcceptNewJobs => IsClusterDurable || !_requiresStrictDistributedMode;

    internal IDistributedJobQueueService JobQueue => _jobQueue;
    internal IDistributedLeaderElection LeaderElection => _leaderElection;
    internal IDistributedProgressStore<MigrationEvidenceProgress> ProgressStore => _progressStore;
    internal IDistributedProgressStore<MigrationEvidenceRequest> RequestStore => _requestStore;
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

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(MigrationEvidenceProgress))]
[JsonSerializable(typeof(MigrationEvidenceRequest))]
internal sealed partial class MigrationEvidenceJobJsonContext : JsonSerializerContext
{
}
