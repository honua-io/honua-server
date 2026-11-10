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
    private readonly string _claimForOneProfile;
    private readonly string _claimForSeveralProfiles;
    private readonly string _findClaimWithLostReply;
    private readonly HashSet<string> _workersWithAnUnconfirmedClaim = new(StringComparer.Ordinal);

    public PostgresJobQueue(JobStoreDatabase database, ILogger<PostgresJobQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _logger = logger;
        _jobs = database.Table(ExecutionJobRow.TableName);

        _claimForOneProfile = ClaimStatement("runtime_profile = @runtime_profile");
        _claimForSeveralProfiles = ClaimStatement("runtime_profile = ANY (@runtime_profiles)");

        _findClaimWithLostReply = $"""
            SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs}
            WHERE claimed_by = @worker_id AND status = '{nameof(ExecutionJobStatus.Provisioning)}'
              AND current_phase = '{ClaimedPhase}'
            ORDER BY claimed_at
            LIMIT 1
            """;
    }

    // One statement selects, locks and advances the job, so two workers cannot both take it and
    // a claim cannot be left half applied. Equality on one runtime profile reads the claimable
    // index in claim order for that profile only.
    private string ClaimStatement(string runtimeProfilePredicate)
        => $"""
            WITH next_job AS (
                SELECT operation_id FROM {_jobs}
                WHERE status = '{nameof(ExecutionJobStatus.Queued)}' AND queue_visible_at <= now()
                  AND {runtimeProfilePredicate}
                  AND (@kinds::text[] IS NULL OR kind = ANY (@kinds))
                ORDER BY queue_priority, queue_visible_at, operation_id
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

    public async Task EnqueueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        // Admission replay and reconciliation repeat enqueue; a job already in the queue keeps its place.
        await ExecuteQueueStatementAsync(
            $"""
            UPDATE {_jobs} SET queue_visible_at = now(), queue_priority = @queue_priority
            WHERE operation_id = @operation_id AND status = '{nameof(ExecutionJobStatus.Queued)}'
              AND backend = '{ExecutionJobRow.LocalBackend}' AND queue_visible_at IS NULL
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
        if (HasUnconfirmedClaim(workerId))
        {
            var recovered = await FindClaimWithLostReplyAsync(workerId, cancellationToken).ConfigureAwait(false);
            ClearUnconfirmedClaim(workerId);
            if (recovered is not null)
            {
                Log.ClaimRecoveredAfterLostReply(_logger, recovered.OperationId, workerId);
                return recovered.OperationId;
            }
        }

        var runtimeProfiles = (acceptedRuntimeProfiles ?? RuntimeProfiles.DefaultAccepted).ToArray();
        ExecutionJobRecord? claimed;
        try
        {
            claimed = await _database.ExecuteAsync(
                async (connection, _, token) =>
                {
                    await using var command = new NpgsqlCommand(
                        runtimeProfiles.Length == 1 ? _claimForOneProfile : _claimForSeveralProfiles, connection);
                    command.Parameters.Add(new NpgsqlParameter("worker_id", NpgsqlDbType.Text) { Value = workerId });
                    command.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text)
                    {
                        Value = acceptedKinds is null ? DBNull.Value : acceptedKinds.Select(kind => kind.ToString()).ToArray()
                    });
                    command.Parameters.Add(runtimeProfiles.Length == 1
                        ? new NpgsqlParameter("runtime_profile", NpgsqlDbType.Text) { Value = runtimeProfiles[0] }
                        : new NpgsqlParameter("runtime_profiles", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = runtimeProfiles });
                    return await ReadJobOrNullAsync(command, token).ConfigureAwait(false);
                },
                StatementRepeat.NotAfterSend,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (JobStoreDatabase.IsTransient(exception))
        {
            // The claim may have committed before its reply was lost. A worker runs one job at a
            // time, so a job left in the claimed phase under its id can only be that claim.
            MarkUnconfirmedClaim(workerId);
            throw;
        }

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
            WHERE operation_id = @operation_id AND backend = '{ExecutionJobRow.LocalBackend}'
              AND status NOT IN ({ExecutionJobRow.TerminalStatusList})
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
            WHERE operation_id = @operation_id AND status = '{nameof(ExecutionJobStatus.Queued)}'
              AND queue_visible_at IS NOT NULL
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

    // Every producer creates a job and enqueues it in a second call. A local job left Queued and
    // out of the queue for longer than the threshold lost that second call, so it is enqueued here.
    public Task ReconcileStaleClaimsAsync(TimeSpan staleThreshold, CancellationToken cancellationToken)
        => _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {_jobs} SET queue_visible_at = now(), queue_priority = priority
                    WHERE status = '{nameof(ExecutionJobStatus.Queued)}' AND queue_visible_at IS NULL
                      AND backend = '{ExecutionJobRow.LocalBackend}' AND NOT cancellation_requested
                      AND updated_at <= now() - @stale_threshold
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("stale_threshold", NpgsqlDbType.Interval) { Value = staleThreshold });
                var enqueued = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (enqueued > 0)
                {
                    Log.UndispatchedJobsEnqueued(_logger, enqueued);
                }
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private async Task<ExecutionJobRecord?> FindClaimWithLostReplyAsync(string workerId, CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(_findClaimWithLostReply, connection);
                command.Parameters.Add(new NpgsqlParameter("worker_id", NpgsqlDbType.Text) { Value = workerId });
                return await ReadJobOrNullAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    private static async Task<ExecutionJobRecord?> ReadJobOrNullAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ExecutionJobRow.Read(reader) : null;
    }

    private bool HasUnconfirmedClaim(string workerId)
    {
        lock (_workersWithAnUnconfirmedClaim)
        {
            return _workersWithAnUnconfirmedClaim.Contains(workerId);
        }
    }

    internal void MarkUnconfirmedClaim(string workerId)
    {
        lock (_workersWithAnUnconfirmedClaim)
        {
            _workersWithAnUnconfirmedClaim.Add(workerId);
        }
    }

    private void ClearUnconfirmedClaim(string workerId)
    {
        lock (_workersWithAnUnconfirmedClaim)
        {
            _workersWithAnUnconfirmedClaim.Remove(workerId);
        }
    }

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
                    command.Parameters.Add(new NpgsqlParameter("queue_priority", NpgsqlDbType.Smallint) { Value = ExecutionJobRow.PriorityBand(priority.Value) });
                }

                if (visibleAfter.HasValue)
                {
                    command.Parameters.Add(new NpgsqlParameter("visible_after", NpgsqlDbType.Interval) { Value = visibleAfter.Value });
                }

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

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

        [LoggerMessage(9024, LogLevel.Warning, "Enqueued {Count} local job(s) that were created but never enqueued")]
        public static partial void UndispatchedJobsEnqueued(ILogger logger, int count);

        [LoggerMessage(9025, LogLevel.Warning, "Recovered claim of {OperationId} by worker {WorkerId} after its reply was lost")]
        public static partial void ClaimRecoveredAfterLostReply(ILogger logger, string operationId, string workerId);
    }
}
