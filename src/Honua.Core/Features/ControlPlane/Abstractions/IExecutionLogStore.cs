// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Durable store for structured execution log entries produced by job executors.
/// Entries are append-only during execution and read-only after terminal state.
/// </summary>
public interface IExecutionLogStore
{
    /// <summary>
    /// Appends a structured log entry for the specified job.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="entry">Log entry to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(
        string operationId,
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all structured log entries for the specified job in chronological order.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Log entries in append order.</returns>
    Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a page of structured log entries for the specified job in append order.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="query">Cursor and page-size criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Log entries in append order plus an optional next cursor.</returns>
    Task<ExecutionLogPage> QueryAsync(
        string operationId,
        ExecutionLogQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a time-to-live on the log entries for the specified job.
    /// Called when a job reaches terminal state to schedule eventual cleanup.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="ttl">Retention period from now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetRetentionAsync(
        string operationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}
