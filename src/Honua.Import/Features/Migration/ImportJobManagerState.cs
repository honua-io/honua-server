// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Migration;

// The cluster-wide stores an import job manager coordinates through. A host with no system database
// has no lease store.
internal sealed record ImportCoordinationStores(IConnectionMultiplexer? Redis = null, ILeaseStore? Leases = null);

internal sealed record ImportJobManagerContext(
    IUniversalProgressStore UniversalProgressStore,
    IDistributedCache? DistributedCache,
    ILogger Logger,
    IHostEnvironment HostEnvironment,
    ImportCoordinationStores Stores);

internal sealed record ImportJobKeys(string Queue, string Leader, string RequestStorePrefix);

internal sealed class ImportJobManagerState<TRequest, TProgress>
    where TRequest : class
    where TProgress : class, IOperationProgress
{
    private readonly IUniversalProgressStore _universalProgressStore;

    public ImportJobManagerState(ImportJobManagerContext context, ImportJobKeys keys, JsonTypeInfo<TRequest> requestJsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.UniversalProgressStore);
        ArgumentNullException.ThrowIfNull(context.Logger);
        ArgumentNullException.ThrowIfNull(context.HostEnvironment);
        ArgumentNullException.ThrowIfNull(context.Stores);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(requestJsonTypeInfo);

        _universalProgressStore = context.UniversalProgressStore;
        RequiresStrictDistributedMode = RedisImportJobManager.RequiresStrictDistributedMode(context.HostEnvironment);

        var redis = context.Stores.Redis;
        JobQueue = new RedisJobQueue(redis, context.Logger, keys.Queue, allowFallback: !RequiresStrictDistributedMode);
        LeaderElection = CreateLeaderElection(context.Stores.Leases, keys.Leader);
        ProgressStore = new DistributedProgressStoreAdapter<TProgress>(context.UniversalProgressStore);
        RequestStore = new RedisProgressStore<TRequest>(
            context.DistributedCache,
            context.Logger,
            keys.RequestStorePrefix,
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
        LeaderElection is LeaseLeaderElection &&
        RequestStore is RedisProgressStore<TRequest> { IsUsingFallback: false } &&
        _universalProgressStore is not UniversalProgressStore { IsUsingFallback: true };

    public bool CanAcceptNewJobs => IsClusterDurable || !RequiresStrictDistributedMode;

    // Without a lease store a development or test host is its own single node; a production host
    // never leads on node-local state.
    private IDistributedLeaderElection CreateLeaderElection(ILeaseStore? leases, string leaderKey)
    {
        if (leases is not null)
        {
            return new LeaseLeaderElection(leases, leaderKey, LeaseLeaderElection.DefaultLeaseDuration);
        }

        return RequiresStrictDistributedMode ? new UnelectedLeaderElection() : new NodeLocalLeaderElection();
    }
}
