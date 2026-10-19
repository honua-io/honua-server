// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Durable store for structured execution log entries produced by job executors.
/// Entries retain append order within a bounded per-job window and expire with job retention.
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

    /// <summary>Atomically appends an external event once per stable producer identity.</summary>
    /// <param name="operationId">Owning job.</param>
    /// <param name="eventId">Server-scoped immutable producer event identity.</param>
    /// <param name="entry">Redacted event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendOnceAsync(string operationId, string eventId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Durable idempotent log ingestion is required.");

    /// <summary>
    /// Retrieves a bounded tail (at most 500 entries in the production store) in append order.
    /// Use TailAsync for total/dropped counts or QueryAsync for explicit cursor pagination.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Log entries in append order.</returns>
    Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the most recent structured log entries without reading the full log stream.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tail entries in append order plus the total number of entries.</returns>
    Task<ExecutionLogTail> TailAsync(
        string operationId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a page of structured log entries for the specified job in append order.
    /// </summary>
    /// <param name="operationId">Stable job identifier.</param>
    /// <param name="query">Cursor and page-size criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Log entries in append order, the stream length from that same read, and an optional next cursor.</returns>
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
