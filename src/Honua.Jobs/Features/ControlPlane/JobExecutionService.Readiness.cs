// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.ControlPlane;

internal sealed partial class JobExecutionService
{
    private bool _hasSuccessfulClaimPoll;
    private bool _readinessStopped;
    private static readonly TimeSpan ReadinessProbeTimeout = TimeSpan.FromSeconds(2);

    // This checks the actual hosted worker, not an independently constructed
    // executor or a registered queue. It grants no submission/execution authority.
    // Remote workers require their own readiness evidence; an assigned-operation
    // worker cannot stand in for this host's persistent queue consumer.
    internal async ValueTask<bool> IsReadyForAsync(IJobQueue expectedQueue,
        IExecutionJobStore expectedStore, ExecutionJobKind kind, string? runtimeProfile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesLiveWorker()) return false;
        try
        {
            var depth = await jobQueue.GetQueueDepthAsync(cancellationToken)
                .WaitAsync(ReadinessProbeTimeout, cancellationToken).ConfigureAwait(false);
            // Stopping may race the probe. Never accept a cached positive result
            // after drain begins or the execution loop has faulted/completed.
            return depth >= 0 && MatchesLiveWorker();
        }
        catch (Exception exception) when (exception is RedisException or IOException or TimeoutException)
        {
            return false;
        }

        bool MatchesLiveWorker() => !Volatile.Read(ref _readinessStopped)
            && !RequeueOnShutdown && Volatile.Read(ref _hasSuccessfulClaimPoll)
            && ExecuteTask is { IsCompleted: false }
            && ReferenceEquals(jobQueue, expectedQueue) && ReferenceEquals(jobStore, expectedStore)
            && ResolveExecutor(kind, runtimeProfile) is { } executor
            && RuntimeProfiles.CanClaim(executor.AcceptedRuntimeProfiles, runtimeProfile);
    }
}
