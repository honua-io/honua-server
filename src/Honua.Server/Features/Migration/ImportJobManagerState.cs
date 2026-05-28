// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Server.Features.Migration;

internal sealed class ImportJobManagerState<TRequest, TProgress>
    where TRequest : class
    where TProgress : class, IOperationProgress
{
    private readonly IUniversalProgressStore _universalProgressStore;

    public ImportJobManagerState(
        IUniversalProgressStore universalProgressStore,
        IDistributedCache? distributedCache,
        ILogger logger,
        IHostEnvironment hostEnvironment,
        IConnectionMultiplexer? redis,
        string queueKey,
        string leaderKey,
        string requestStorePrefix,
        JsonTypeInfo<TRequest> requestJsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(universalProgressStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(queueKey);
        ArgumentNullException.ThrowIfNull(leaderKey);
        ArgumentNullException.ThrowIfNull(requestStorePrefix);
        ArgumentNullException.ThrowIfNull(requestJsonTypeInfo);

        _universalProgressStore = universalProgressStore;
        RequiresStrictDistributedMode = RedisImportJobManager.RequiresStrictDistributedMode(hostEnvironment);

        var instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
        JobQueue = new RedisJobQueue(redis, logger, queueKey, allowFallback: !RequiresStrictDistributedMode);
        LeaderElection = new RedisLeaderElection(
            redis,
            logger,
            leaderKey,
            instanceId,
            allowLocalFallbackLeadership: !RequiresStrictDistributedMode);
        ProgressStore = new DistributedProgressStoreAdapter<TProgress>(universalProgressStore);
        RequestStore = new RedisProgressStore<TRequest>(
            distributedCache,
            logger,
            requestStorePrefix,
            requestJsonTypeInfo,
            redis);
    }

    public bool RequiresStrictDistributedMode { get; }

    public IDistributedJobQueueService JobQueue { get; }

    public IDistributedLeaderElection LeaderElection { get; }

    public IDistributedProgressStore<TProgress> ProgressStore { get; }

    public IDistributedProgressStore<TRequest> RequestStore { get; }

    public bool IsClusterDurable =>
        JobQueue is RedisJobQueue { IsUsingFallback: false } &&
        LeaderElection is RedisLeaderElection { IsUsingFallback: false } &&
        RequestStore is RedisProgressStore<TRequest> { IsUsingFallback: false } &&
        _universalProgressStore is not UniversalProgressStore { IsUsingFallback: true };

    public bool CanAcceptNewJobs => IsClusterDurable || !RequiresStrictDistributedMode;
}
