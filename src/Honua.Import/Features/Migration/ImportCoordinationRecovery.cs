// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Migration;

/// <summary>
/// Restores cached import-coordination health after Redis returns (honua-server#4877).
/// </summary>
/// <remarks>
/// <see cref="IImportCoordinationHealth.CanAcceptNewJobs"/> reads per-component
/// durability flags that only a component's own operations can clear. Every admission
/// gate (worker loop and start endpoints) therefore probes the unavailable components
/// before trusting the flag; gating first would keep admission closed forever. Each
/// component recovers through its own channel — the queue through the queue connection,
/// the request and progress stores through their distributed cache — so one healthy
/// connection never declares the others recovered. Probes never write, never dequeue,
/// never enable local fallback, and are throttled by each component's retry interval.
/// </remarks>
internal static class ImportCoordinationRecovery
{
    private static readonly TimeSpan _componentProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Probes every coordination component, bounding each probe so one hung dependency
    /// cannot stall recovery of the others or the caller.
    /// </summary>
    public static async Task ProbeAsync<TRequest, TProgress>(
        IDistributedJobQueueService jobQueue,
        IDistributedProgressStore<TRequest> requestStore,
        IDistributedProgressStore<TProgress> progressStore,
        CancellationToken cancellationToken)
        where TRequest : class
        where TProgress : class
    {
        // The queue length read is the queue's throttled restore path; it never dequeues.
        await ProbeComponentAsync(jobQueue.GetQueueLengthAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        if (requestStore is IProgressStoreRecovery requestRecovery)
        {
            await ProbeComponentAsync(requestRecovery.ProbeRecoveryAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        if (progressStore is IProgressStoreRecovery progressRecovery)
        {
            await ProbeComponentAsync(progressRecovery.ProbeRecoveryAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns whether a new job may be admitted, first probing recovery when the cached
    /// coordination health reports an outage. Still-unavailable coordination is refused.
    /// </summary>
    public static async Task<bool> TryAdmitAsync<TRequest, TProgress>(
        IImportCoordinationHealth health,
        IDistributedJobQueueService jobQueue,
        IDistributedProgressStore<TRequest> requestStore,
        IDistributedProgressStore<TProgress> progressStore,
        CancellationToken cancellationToken)
        where TRequest : class
        where TProgress : class
    {
        if (health.CanAcceptNewJobs)
        {
            return true;
        }

        await ProbeAsync(jobQueue, requestStore, progressStore, cancellationToken).ConfigureAwait(false);
        return health.CanAcceptNewJobs;
    }

    private static async Task ProbeComponentAsync(Task probe, CancellationToken cancellationToken)
    {
        try
        {
            await probe.WaitAsync(_componentProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still unavailable: the component keeps its cached failure flag, and its own
            // retry interval throttles the next attempt. Move on to the next component.
        }
    }
}
