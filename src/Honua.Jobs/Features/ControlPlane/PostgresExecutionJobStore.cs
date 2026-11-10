// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

// Execution jobs in PostgreSQL (ADR-0081). Every statement is its own transaction and every
// expiry and stamp uses the database clock.
internal sealed partial class PostgresExecutionJobStore : IExecutionJobStore, IJobStoreClock, IReconcilableJobSource
{
    private const int MinQueryLimit = 1;
    private const int MaxQueryLimit = 200;
    private const int ActiveListOverfetchFactor = 2;
    private const string InvalidCursorMessage = "Invalid job cursor.";

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

    public Task<bool> TryAcquireLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => ExecuteLeaseStatementAsync(
            $"""
            INSERT INTO {_leases} AS lease (lease_key, owner_id, expires_at)
            VALUES (@lease_key, @lease_owner_id, now() + @duration)
            ON CONFLICT (lease_key) DO UPDATE SET owner_id = EXCLUDED.owner_id, expires_at = EXCLUDED.expires_at
            WHERE lease.expires_at <= now()
            """,
            operationId,
            ownerId,
            leaseDuration,
            StatementRepeat.NotAfterSend,
            cancellationToken);

    public Task<bool> RenewLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => ExecuteLeaseStatementAsync(
            $"""
            UPDATE {_leases} SET expires_at = now() + @duration
            WHERE lease_key = @lease_key AND owner_id = @lease_owner_id AND expires_at > now()
            """,
            operationId,
            ownerId,
            leaseDuration,
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task ReleaseLeaseAsync(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken = default)
        => ExecuteLeaseStatementAsync(
            $"DELETE FROM {_leases} WHERE lease_key = @lease_key AND owner_id = @lease_owner_id",
            operationId,
            ownerId,
            leaseDuration: null,
            StatementRepeat.SafeToRepeat,
            cancellationToken);

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

    public Task<bool> TrySetIfLeaseOwnedAsync(
        ExecutionJobRecord job,
        string leaseOperationId,
        string leaseOwnerId,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwnerId);
        return CompareAndSetAsync(job, ttl, new OwnedLease(leaseOperationId, leaseOwnerId), cancellationToken);
    }

    public async Task<ExecutionJobPage> QueryAsync(
        ExecutionJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        ExecutionJobCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!ExecutionJobCursor.TryDecode(query.Cursor, out var decoded))
            {
                throw new ArgumentException(InvalidCursorMessage, nameof(query));
            }

            cursor = decoded;
        }

        var limit = Math.Clamp(query.Limit, MinQueryLimit, MaxQueryLimit);
        var jobs = await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = BuildQueryCommand(connection, query, cursor, limit);
                return await ReadJobsAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

        if (jobs.Count <= limit)
        {
            return new ExecutionJobPage { Items = jobs };
        }

        jobs.RemoveAt(jobs.Count - 1);
        var last = jobs[^1];
        return new ExecutionJobPage
        {
            Items = jobs,
            NextCursor = new ExecutionJobCursor(last.CreatedAt, last.OperationId).Encode()
        };
    }

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                var text = new StringBuilder(
                    $"SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs} WHERE status IN ({ExecutionJobRow.ActiveStatusList})");
                await using var command = new NpgsqlCommand { Connection = connection };
                if (kind.HasValue)
                {
                    text.Append(" AND kind = @kind");
                    command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = kind.Value.ToString() });
                }

                text.Append(" ORDER BY updated_at DESC");
                if (limit.HasValue)
                {
                    text.Append(" LIMIT @limit");
                    command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Bigint)
                    {
                        Value = (long)limit.Value * ActiveListOverfetchFactor
                    });
                }

                command.CommandText = text.ToString();
                return await ReadJobsAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListExpiryAndDispatchCandidatesAsync(CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs}
                    WHERE (status IN ('{nameof(ExecutionJobStatus.Provisioning)}', '{nameof(ExecutionJobStatus.Running)}')
                           AND (COALESCE(last_heartbeat_at, claimed_at) <= now() - heartbeat_timeout
                                OR claimed_at <= now() - max_duration))
                       OR (status = '{nameof(ExecutionJobStatus.Queued)}' AND queue_visible_at IS NULL
                           AND backend = '{ExecutionJobRow.LocalBackend}')
                    """,
                    connection);
                return await ReadJobsAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListBackendReconciliationCandidateIdsAsync(CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                // The predicate is the one idx_execution_jobs_reconcilable is built on.
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT operation_id FROM {_jobs}
                    WHERE status IN ({ExecutionJobRow.ActiveStatusList})
                      AND NOT (status = '{nameof(ExecutionJobStatus.Queued)}' AND backend = '{ExecutionJobRow.LocalBackend}'
                               AND attempt_count = 0 AND NOT cancellation_requested)
                    """,
                    connection);
                var operationIds = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    operationIds.Add(reader.GetString(0));
                }

                return operationIds;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT now()", connection);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await reader.ReadAsync(token).ConfigureAwait(false);
                return reader.GetFieldValue<DateTimeOffset>(0);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

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

    private async Task<bool> ExecuteLeaseStatementAsync(
        string statement,
        string leaseKey,
        string ownerId,
        TimeSpan? leaseDuration,
        StatementRepeat repeat,
        CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                command.Parameters.Add(new NpgsqlParameter("lease_key", NpgsqlDbType.Text) { Value = leaseKey });
                command.Parameters.Add(new NpgsqlParameter("lease_owner_id", NpgsqlDbType.Text) { Value = ownerId });
                if (leaseDuration.HasValue)
                {
                    command.Parameters.Add(new NpgsqlParameter("duration", NpgsqlDbType.Interval) { Value = leaseDuration.Value });
                }

                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            repeat,
            cancellationToken).ConfigureAwait(false);

    private NpgsqlCommand BuildQueryCommand(
        NpgsqlConnection connection,
        ExecutionJobQuery query,
        ExecutionJobCursor? cursor,
        int limit)
    {
        var command = new NpgsqlCommand { Connection = connection };
        var predicates = new List<string>();

        if (query.Statuses.Count > 0)
        {
            predicates.Add("status = ANY (@statuses)");
            command.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = query.Statuses.Select(status => status.ToString()).Distinct().ToArray()
            });
        }

        if (query.Kind.HasValue)
        {
            predicates.Add("kind = @kind");
            command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = query.Kind.Value.ToString() });
        }

        if (query.ApplyTenantScope)
        {
            predicates.Add("tenant_id IS NOT DISTINCT FROM @tenant_id");
            command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Text) { Value = (object?)query.TenantId ?? DBNull.Value });
        }

        if (query.CreatedFrom.HasValue)
        {
            predicates.Add("created_at >= @created_from");
            command.Parameters.Add(ExecutionJobRow.Timestamp("created_from", query.CreatedFrom));
        }

        if (query.CreatedTo.HasValue)
        {
            predicates.Add("created_at < @created_to");
            command.Parameters.Add(ExecutionJobRow.Timestamp("created_to", query.CreatedTo));
        }

        var requiredLookupKeys = RequiredLookupKeys(query);
        if (requiredLookupKeys.Length > 0)
        {
            predicates.Add("lookup_keys @> @required_lookup_keys");
            command.Parameters.Add(new NpgsqlParameter("required_lookup_keys", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = requiredLookupKeys
            });
        }

        if (cursor is { } position)
        {
            predicates.Add("(created_at, operation_id) < (@cursor_created_at, @cursor_operation_id)");
            command.Parameters.Add(ExecutionJobRow.Timestamp("cursor_created_at", position.CreatedAt));
            command.Parameters.Add(new NpgsqlParameter("cursor_operation_id", NpgsqlDbType.Text) { Value = position.OperationId });
        }

        var text = new StringBuilder($"SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs}");
        if (predicates.Count > 0)
        {
            text.Append(" WHERE ").AppendJoin(" AND ", predicates);
        }

        text.Append(" ORDER BY created_at DESC, operation_id DESC LIMIT @limit");
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit + 1 });
        command.CommandText = text.ToString();
        return command;
    }

    private static string[] RequiredLookupKeys(ExecutionJobQuery query)
    {
        var keys = new List<string>();
        Add(ExecutionJobLookupDimensions.Backend, query.Backend);
        Add(ExecutionJobLookupDimensions.Queue, query.Queue);
        Add(ExecutionJobLookupDimensions.RequestedBy, query.RequestedBy);
        Add(ExecutionJobLookupDimensions.Correlation, query.CorrelationId);
        Add(ExecutionJobLookupDimensions.Trace, query.TraceId);
        Add(ExecutionJobLookupDimensions.Definition, query.DefinitionId);
        Add(ExecutionJobLookupDimensions.Resource, query.ResourceRef);
        Add(ExecutionJobLookupDimensions.Environment, query.Environment);
        Add(ExecutionJobLookupDimensions.Server, query.Server);
        Add(ExecutionJobLookupDimensions.Release, query.ReleaseId);
        Add(ExecutionJobLookupDimensions.ChangeSet, query.ChangeSetId);
        Add(ExecutionJobLookupDimensions.Alert, query.AlertId);
        return [.. keys];

        void Add(string dimension, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                keys.Add(ExecutionJobRow.LookupKey(dimension, value));
            }
        }
    }

    private static async Task<List<ExecutionJobRecord>> ReadJobsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var jobs = new List<ExecutionJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ExecutionJobRow.Read(reader));
        }

        return jobs;
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

    private readonly record struct OwnedLease(string LeaseKey, string OwnerId);

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
