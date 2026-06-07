// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Abstractions;

/// <summary>
/// Submits a corrective-operation work item to the durable job runner and returns its job id (slice 5 of
/// honua-server#1166). This isolates <see cref="ITemporalRollbackRunner"/> from the concrete job-runner
/// wiring: the default implementation tracks the corrective operation as an in-process background job; a
/// deployment with the durable control plane (Redis-backed) registers a sink that enqueues onto it.
/// </summary>
public interface ITemporalCorrectiveJobSink
{
    /// <summary>
    /// Submits a corrective operation for asynchronous execution and returns a job id plus its initial
    /// status. The supplied work delegate performs the forward corrective edits through the canonical edit
    /// pipeline; it must never delete change-log history.
    /// </summary>
    /// <param name="operationName">Stable operation name recorded for the job (for example <c>temporal.rollback</c>).</param>
    /// <param name="serviceId">Owning service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="work">The corrective work to run; receives a cancellation token bound to the job.</param>
    /// <param name="cancellationToken">Cancellation token for the submission itself (not the job run).</param>
    /// <returns>The submitted job's id and initial status.</returns>
    Task<(string JobId, string Status)> SubmitAsync(
        string operationName,
        string serviceId,
        int layerId,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}
