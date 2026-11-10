// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

/// <summary>
/// The local job queue as a query over <c>execution_jobs</c> (ADR-0081). A job is in the queue
/// while <c>queue_visible_at</c> is set and claimable while it is also <c>Queued</c> and visible.
/// </summary>
internal sealed partial class PostgresJobQueue : IJobQueue, IQueueClaimReconciler
{
    internal const string ClaimedPhase = "Claimed";

    private readonly JobStoreDatabase _database;
    private readonly ILogger<PostgresJobQueue> _logger;
    private readonly string _jobs;
    private readonly string _claim;

    public PostgresJobQueue(JobStoreDatabase database, ILogger<PostgresJobQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _logger = logger;
        _jobs = database.Table(ExecutionJobRow.TableName);

        // One statement selects, locks and advances the job, so two workers cannot both take it
        // and a claim cannot be left half applied.
        _claim = $"""
            WITH next_job AS (
                SELECT operation_id FROM {_jobs}
                WHERE status = '{nameof(ExecutionJobStatus.Queued)}' AND queue_visible_at <= now()
                  AND (@kinds::text[] IS NULL OR kind = ANY (@kinds))
                  AND runtime_profile = ANY (@runtime_profiles)
                ORDER BY queue_priority, created_at, operation_id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            UPDATE {_jobs} AS job
            SET status = '{nameof(ExecutionJobStatus.Provisioning)}',
                version = job.version + 1,
                attempt_count = job.attempt_count + 1,
                claimed_by = @worker_id,
                claimed_at = now(),
                last_heartbeat_at = now(),
                next_retry_at = NULL,
                current_phase = '{ClaimedPhase}',
                updated_at = now()
            FROM next_job
            WHERE job.operation_id = next_job.operation_id
            RETURNING {ExecutionJobRow.SelectColumns}
            """;
    }

    public async Task EnqueueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        // Admission replay and reconciliation repeat enqueue; a job already in the queue keeps its place.
        await ExecuteQueueStatementAsync(
            $"""
            UPDATE {_jobs} SET queue_visible_at = now(), queue_priority = @queue_priority
            WHERE operation_id = @operation_id AND status = '{nameof(ExecutionJobStatus.Queued)}' AND queue_visible_at IS NULL
            """,
            operationId,
            priority,
            visibleAfter: null,
            cancellationToken).ConfigureAwait(false);

        Log.JobEnqueued(_logger, operationId, priority.ToString());
    }

    public async Task<string?> TryClaimAsync(
        string workerId,
        IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
        IReadOnlySet<string>? acceptedRuntimeProfiles = null,
        CancellationToken cancellationToken = default)
    {
        var claimed = await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(_claim, connection);
                command.Parameters.Add(new NpgsqlParameter("worker_id", NpgsqlDbType.Text) { Value = workerId });
                command.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = acceptedKinds is null ? DBNull.Value : acceptedKinds.Select(kind => kind.ToString()).ToArray()
                });
                command.Parameters.Add(new NpgsqlParameter("runtime_profiles", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = (acceptedRuntimeProfiles ?? RuntimeProfiles.DefaultAccepted).ToArray()
                });
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ExecutionJobRow.Read(reader) : null;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken).ConfigureAwait(false);

        if (claimed is null)
        {
            return null;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(claimed with { Status = ExecutionJobStatus.Queued }, claimed);
        Log.JobClaimed(_logger, claimed.OperationId, workerId);
        return claimed.OperationId;
    }

    public async Task RequeueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        TimeSpan? visibleAfter = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteQueueStatementAsync(
            $"""
            UPDATE {_jobs} SET queue_visible_at = now() + @visible_after, queue_priority = @queue_priority
            WHERE operation_id = @operation_id AND status NOT IN ({ExecutionJobRow.TerminalStatusList})
            """,
            operationId,
            priority,
            visibleAfter is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);

        Log.JobRequeued(_logger, operationId);
    }

    public async Task RemoveAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        await ExecuteQueueStatementAsync(
            $"""
            UPDATE {_jobs} SET queue_visible_at = NULL, queue_priority = NULL
            WHERE operation_id = @operation_id AND queue_visible_at IS NOT NULL
            """,
            operationId,
            priority: null,
            visibleAfter: null,
            cancellationToken).ConfigureAwait(false);

        Log.JobRemovedFromQueue(_logger, operationId);
    }

    public async Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT count(*) FROM {_jobs}
                    WHERE status = '{nameof(ExecutionJobStatus.Queued)}' AND queue_visible_at IS NOT NULL
                    """,
                    connection);
                return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    // Nothing to repair: the claim is one statement, and a claimed job written back to Queued
    // re-enters the queue in the statement that changes its status.
    public Task ReconcileStaleClaimsAsync(TimeSpan staleThreshold, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private Task ExecuteQueueStatementAsync(
        string statement,
        string operationId,
        OperationPriority? priority,
        TimeSpan? visibleAfter,
        CancellationToken cancellationToken)
        => _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operationId });
                if (priority.HasValue)
                {
                    command.Parameters.Add(new NpgsqlParameter("queue_priority", NpgsqlDbType.Smallint) { Value = PriorityBand(priority.Value) });
                }

                if (visibleAfter.HasValue)
                {
                    command.Parameters.Add(new NpgsqlParameter("visible_after", NpgsqlDbType.Interval) { Value = visibleAfter.Value });
                }

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static short PriorityBand(OperationPriority priority)
        => priority switch
        {
            OperationPriority.Critical => 0,
            OperationPriority.High => 1,
            OperationPriority.Low => 3,
            _ => 2
        };

    private static partial class Log
    {
        [LoggerMessage(9020, LogLevel.Information, "Job enqueued: {OperationId}, Priority={Priority}")]
        public static partial void JobEnqueued(ILogger logger, string operationId, string priority);

        [LoggerMessage(9021, LogLevel.Information, "Job claimed: {OperationId} by worker {WorkerId}")]
        public static partial void JobClaimed(ILogger logger, string operationId, string workerId);

        [LoggerMessage(9022, LogLevel.Information, "Job requeued: {OperationId}")]
        public static partial void JobRequeued(ILogger logger, string operationId);

        [LoggerMessage(9023, LogLevel.Debug, "Job removed from queue: {OperationId}")]
        public static partial void JobRemovedFromQueue(ILogger logger, string operationId);
    }
}
