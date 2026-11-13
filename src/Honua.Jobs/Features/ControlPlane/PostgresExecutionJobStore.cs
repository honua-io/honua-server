// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

// Execution jobs in PostgreSQL (ADR-0081). Every statement is its own transaction and every
// expiry and stamp uses the database clock.
internal sealed partial class PostgresExecutionJobStore : IExecutionJobStore, IJobStoreClock, IReconcilableJobSource, IAdmissionSubmissionCounter, IBoundedExecutionJobStore
{
    // A compare-and-set write turns a timestamp the writer changed into the database time and
    // keeps one the writer left as it read it. A job written back to Queued after a local worker
    // claimed it re-enters the queue here, in the statement that changes its status.
    private static readonly string _compareAndSetAssignments = $"""
        version = version + 1,
        status = @status,
        attempt_count = @attempt_count,
        claimed_by = @claimed_by,
        percent_complete = @percent_complete,
        current_phase = @current_phase,
        updated_at = now(),
        claimed_at = {DatabaseStamp("claimed_at")},
        last_heartbeat_at = {DatabaseStamp("last_heartbeat_at")},
        completed_at = {DatabaseStamp("completed_at")},
        next_retry_at = CASE WHEN @next_retry_at IS NULL THEN NULL
                             WHEN next_retry_at IS NOT DISTINCT FROM @next_retry_at THEN next_retry_at
                             ELSE now() + @retry_delay END,
        queue_visible_at = CASE WHEN @is_terminal THEN NULL
                                WHEN @status = '{nameof(ExecutionJobStatus.Queued)}'
                                     AND status <> '{nameof(ExecutionJobStatus.Queued)}'
                                     AND queue_visible_at IS NOT NULL THEN now() + @retry_delay
                                ELSE queue_visible_at END,
        queue_priority = CASE WHEN @is_terminal THEN NULL ELSE queue_priority END,
        expires_at = now() + @retention,
        {ExecutionJobRow.RecordAssignments}
        """;

    private readonly JobStoreDatabase _database;
    private readonly JobStoreOptions _options;
    private readonly ILogger<PostgresExecutionJobStore> _logger;
    private readonly string _jobs;
    private readonly string _leases;
    private readonly string _insertJob;
    private readonly string _compareAndSet;
    private readonly string _ownedLeasePrefix;

    public PostgresExecutionJobStore(
        JobStoreDatabase database,
        IOptions<JobStoreOptions> options,
        ILogger<PostgresExecutionJobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        _database = database;
        _options = options.Value;
        _logger = logger;
        _jobs = database.Table(ExecutionJobRow.TableName);
        _leases = database.Table("leases");

        _insertJob = $"""
            INSERT INTO {_jobs} AS job (operation_id, version, status, attempt_count, claimed_by, claimed_at,
                last_heartbeat_at, next_retry_at, completed_at, percent_complete, current_phase, updated_at,
                expires_at, {ExecutionJobRow.RecordColumns})
            VALUES (@operation_id, @version, @status, @attempt_count, @claimed_by, @claimed_at,
                @last_heartbeat_at, @next_retry_at, @completed_at, @percent_complete, @current_phase, @updated_at,
                now() + @retention, {ExecutionJobRow.RecordParameters})
            """;

        _compareAndSet = $"""
            UPDATE {_jobs} SET
            {_compareAndSetAssignments}
            WHERE operation_id = @operation_id AND version = @expected_version
            """;

        _ownedLeasePrefix = $"""
            WITH owned_lease AS MATERIALIZED (
                SELECT 1 FROM {_leases}
                WHERE lease_key = @lease_key AND owner_id = @lease_owner_id AND expires_at > now()
                FOR UPDATE)

            """;
    }

    public async Task<bool> TryCreateAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var created = job with { Version = 1 };
        var row = ExecutionJobRow.From(created);
        var inserted = await _database.ExecuteAsync(
            async (connection, attempt, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"{_insertJob} ON CONFLICT (operation_id) DO NOTHING", connection);
                AddInsertParameters(command, created, row, ttl);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1)
                {
                    return true;
                }

                return attempt.EarlierAttemptMayHaveCommitted
                    && await IsStoredAsync(connection, created, row, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

        if (inserted)
        {
            Log.ExecutionJobCreated(_logger, job.OperationId, created.Spec.Kind.ToString(), created.Status.ToString());
        }

        return inserted;
    }

    public async Task<ExecutionJobRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs} WHERE operation_id = @operation_id",
                    connection);
                command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operationId });
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ExecutionJobRow.Read(reader) : null;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task SetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var stored = job with { Version = job.Version + 1 };
        var row = ExecutionJobRow.From(stored);
        await _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    {_insertJob}
                    ON CONFLICT (operation_id) DO UPDATE SET
                        version = EXCLUDED.version, status = EXCLUDED.status, attempt_count = EXCLUDED.attempt_count,
                        claimed_by = EXCLUDED.claimed_by, claimed_at = EXCLUDED.claimed_at,
                        last_heartbeat_at = EXCLUDED.last_heartbeat_at, next_retry_at = EXCLUDED.next_retry_at,
                        completed_at = EXCLUDED.completed_at,
                        percent_complete = EXCLUDED.percent_complete, current_phase = EXCLUDED.current_phase,
                        updated_at = EXCLUDED.updated_at, expires_at = EXCLUDED.expires_at,
                        {ExecutionJobRow.RecordAssignments},
                        queue_visible_at = CASE WHEN @is_terminal THEN NULL ELSE job.queue_visible_at END,
                        queue_priority = CASE WHEN @is_terminal THEN NULL ELSE job.queue_priority END
                    """,
                    connection);
                AddInsertParameters(command, stored, row, ttl);
                command.Parameters.Add(IsTerminalParameter(stored));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

        Log.ExecutionJobUpdated(_logger, job.OperationId, stored.Status.ToString());
    }

    public Task<bool> TrySetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
        => CompareAndSetAsync(job, ttl, ownedLease: null, cancellationToken);

    private async Task<bool> CompareAndSetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl,
        OwnedLease? ownedLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var row = ExecutionJobRow.From(job);
        var leasePrefix = ownedLease is null ? string.Empty : _ownedLeasePrefix;
        var leaseCondition = ownedLease is null ? string.Empty : " AND EXISTS (SELECT 1 FROM owned_lease)";

        var updated = await _database.ExecuteAsync(
            async (connection, attempt, token) =>
            {
                await using (var command = new NpgsqlCommand($"{leasePrefix}{_compareAndSet}{leaseCondition}", connection))
                {
                    AddAttemptStateParameters(command, job);
                    row.AddRecordParameters(command);
                    command.Parameters.Add(new NpgsqlParameter("expected_version", NpgsqlDbType.Bigint) { Value = job.Version });
                    command.Parameters.Add(new NpgsqlParameter("retry_delay", NpgsqlDbType.Interval) { Value = RetryDelay(job) });
                    command.Parameters.Add(IsTerminalParameter(job));
                    command.Parameters.Add(RetentionParameter(ttl));
                    if (ownedLease is { } lease)
                    {
                        command.Parameters.Add(new NpgsqlParameter("lease_key", NpgsqlDbType.Text) { Value = lease.LeaseKey });
                        command.Parameters.Add(new NpgsqlParameter("lease_owner_id", NpgsqlDbType.Text) { Value = lease.OwnerId });
                    }

                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1)
                    {
                        return true;
                    }
                }

                return attempt.EarlierAttemptMayHaveCommitted
                    && await IsStoredAsync(connection, job with { Version = job.Version + 1 }, row, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

        if (updated)
        {
            Log.ExecutionJobUpdated(_logger, job.OperationId, job.Status.ToString());
        }
        else
        {
            Log.CasConflict(_logger, job.OperationId, job.Version);
        }

        return updated;
    }

    // After a statement that may have committed before its reply was lost: the write landed if
    // the row is at the version it would have produced and holds exactly the record that was sent.
    private async Task<bool> IsStoredAsync(
        NpgsqlConnection connection,
        ExecutionJobRecord expected,
        ExecutionJobRow row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM {_jobs} WHERE operation_id = @operation_id AND version = @version AND record::text = @record_text",
            connection);
        command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = expected.OperationId });
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = expected.Version });
        command.Parameters.Add(new NpgsqlParameter("record_text", NpgsqlDbType.Text) { Value = row.RecordJson });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private void AddInsertParameters(NpgsqlCommand command, ExecutionJobRecord job, ExecutionJobRow row, TimeSpan? ttl)
    {
        AddAttemptStateParameters(command, job);
        row.AddRecordParameters(command);
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = job.Version });
        command.Parameters.Add(ExecutionJobRow.Timestamp("updated_at", job.UpdatedAt));
        command.Parameters.Add(RetentionParameter(ttl));
    }

    private static void AddAttemptStateParameters(NpgsqlCommand command, ExecutionJobRecord job)
    {
        command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = job.OperationId });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = job.Status.ToString() });
        command.Parameters.Add(new NpgsqlParameter("attempt_count", NpgsqlDbType.Integer) { Value = job.AttemptCount });
        command.Parameters.Add(new NpgsqlParameter("claimed_by", NpgsqlDbType.Text) { Value = (object?)job.ClaimedBy ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("percent_complete", NpgsqlDbType.Double) { Value = (object?)job.PercentComplete ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("current_phase", NpgsqlDbType.Text) { Value = (object?)job.CurrentPhase ?? DBNull.Value });
        command.Parameters.Add(ExecutionJobRow.Timestamp("claimed_at", job.ClaimedAt));
        command.Parameters.Add(ExecutionJobRow.Timestamp("last_heartbeat_at", job.LastHeartbeatAt));
        command.Parameters.Add(ExecutionJobRow.Timestamp("next_retry_at", job.NextRetryAt));
        command.Parameters.Add(ExecutionJobRow.Timestamp("completed_at", job.CompletedAt));
    }

    private static string DatabaseStamp(string column)
        => $"CASE WHEN @{column} IS NULL THEN NULL WHEN {column} IS NOT DISTINCT FROM @{column} THEN {column} ELSE now() END";

    // The writer states the delay on its own clock; the statement adds it to the database clock.
    private static TimeSpan RetryDelay(ExecutionJobRecord job)
        => job.NextRetryAt is { } nextRetryAt && nextRetryAt > job.UpdatedAt
            ? nextRetryAt - job.UpdatedAt
            : TimeSpan.Zero;

    private static NpgsqlParameter IsTerminalParameter(ExecutionJobRecord job)
        => new("is_terminal", NpgsqlDbType.Boolean) { Value = ExecutionJobRow.IsTerminal(job.Status) };

    private NpgsqlParameter RetentionParameter(TimeSpan? ttl)
        => new("retention", NpgsqlDbType.Interval) { Value = ttl ?? _options.JobRetention };

    private static partial class Log
    {
        [LoggerMessage(9010, LogLevel.Information, "Created execution job {OperationId} ({Kind}) with status {Status}")]
        public static partial void ExecutionJobCreated(ILogger logger, string operationId, string kind, string status);

        [LoggerMessage(9011, LogLevel.Debug, "Updated execution job {OperationId} to status {Status}")]
        public static partial void ExecutionJobUpdated(ILogger logger, string operationId, string status);

        [LoggerMessage(9012, LogLevel.Debug, "Version conflict for execution job {OperationId} at version {Version}; a concurrent write was detected")]
        public static partial void CasConflict(ILogger logger, string operationId, long version);
    }
}
