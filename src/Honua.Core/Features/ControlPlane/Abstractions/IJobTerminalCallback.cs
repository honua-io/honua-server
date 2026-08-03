// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Receives notification when a durable execution job transitions to a terminal
/// state. Implementations synchronize feature-specific side-effect stores
/// (e.g. admin progress) with the authoritative execution job record.
/// </summary>
/// <remarks>
/// Callbacks are invoked best-effort after the authoritative store write.
/// A failing callback must not block the terminal transition.
/// </remarks>
public interface IJobTerminalCallback
{
    /// <summary>
    /// Called after a job has been durably written to a terminal status.
    /// </summary>
    ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken);
}

/// <summary>
/// Marks a terminal callback whose idempotent side effects may be re-driven from a durable
/// projection-pending record after the job itself has already reached a terminal state.
/// </summary>
public interface IRetryableJobTerminalCallback : IJobTerminalCallback
{
}

/// <summary>
/// Durable outbox for terminal projections that must survive callback, process, or host failure.
/// Implementations enqueue terminal jobs as part of durable job-state indexing and retain them
/// until the retryable callback explicitly acknowledges that every required projection completed.
/// </summary>
public interface ITerminalProjectionRetryStore
{
    /// <summary>Returns a bounded set of terminal jobs whose side effects remain pending.</summary>
    Task<IReadOnlyList<ExecutionJobRecord>> ListTerminalProjectionsPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges completion of every required terminal projection for one job.</summary>
    Task CompleteTerminalProjectionAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}
