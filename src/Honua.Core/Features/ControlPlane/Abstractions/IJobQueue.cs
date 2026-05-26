// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Durable job queue with atomic claim semantics for distributing execution jobs
/// across worker instances. The queue mediates the boundary between the API-facing
/// submission path and the worker-facing execution path.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Queue accurately describes the durable job distribution primitive")]
public interface IJobQueue
{
    /// <summary>
    /// Enqueues a job for execution. The job must already be persisted in
    /// <see cref="IExecutionJobStore"/> before enqueuing.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="priority">Relative priority influencing dequeue order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnqueueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the next available job that matches the requested kinds.
    /// The claim associates the job with a <paramref name="workerId"/> and sets
    /// a lease that must be renewed through heartbeats.
    /// </summary>
    /// <param name="workerId">Stable identifier of the claiming worker.</param>
    /// <param name="acceptedKinds">
    /// Optional filter for job kinds this worker can execute.
    /// Null accepts all kinds.
    /// </param>
    /// <param name="acceptedRuntimeProfiles">
    /// Runtime-profile claim fence: the set of runtime profiles this worker can
    /// execute. A job is only claimable when its effective profile is in this set,
    /// where a job whose <see cref="ExecutionJobSpec.RuntimeProfile"/> is null or empty
    /// is treated as the managed/default profile (see
    /// <see cref="Domain.RuntimeProfiles.Normalize(string?)"/>). A <c>null</c> set is
    /// treated as <see cref="Domain.RuntimeProfiles.DefaultAccepted"/>
    /// (managed/default only) — NOT "accept any" — so a worker that declares no
    /// profile constraint can never claim a <see cref="Domain.RuntimeProfiles.Native"/>
    /// job. This is the load-bearing guard that keeps the lean (GDAL-free) worker from
    /// claiming a native GDAL job, and the native worker from claiming managed jobs.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claimed job identifier, or null if the queue is empty.</returns>
    Task<string?> TryClaimAsync(
        string workerId,
        IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
        IReadOnlySet<string>? acceptedRuntimeProfiles = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-enqueues a job that was previously claimed. Used when a worker abandons
    /// a job or the reconciler detects an expired heartbeat and the retry policy
    /// permits another attempt.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="priority">Priority for the re-enqueued attempt.</param>
    /// <param name="visibleAfter">
    /// Optional delay before the job becomes visible for claiming again,
    /// used to implement backoff between retries.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequeueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        TimeSpan? visibleAfter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a job from the queue. Called when a job reaches a terminal state
    /// (succeeded, failed without remaining retries, or cancelled).
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the approximate number of jobs currently in the queue.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default);
}
