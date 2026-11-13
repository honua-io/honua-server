// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed partial class PostgresExecutionJobStore
{
    public async Task<bool> TryCreateWithinCapacityAsync(
        ExecutionJobRecord job,
        int maximumOutstanding,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutstanding);
        var created = job with { Version = 1 };
        var row = ExecutionJobRow.From(created);
        return await _database.ExecuteAsync(async (connection, attempt, token) =>
        {
            if (attempt.EarlierAttemptMayHaveCommitted
                && await IsStoredAsync(connection, created, row, token).ConfigureAwait(false))
            {
                return true;
            }

            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await LockCapacityAsync(transaction, row.Kind, token).ConfigureAwait(false);
            if (await CountOutstandingAsync(transaction, row.Kind, token).ConfigureAwait(false) >= maximumOutstanding)
            {
                return false;
            }

            await using var command = new NpgsqlCommand($"""
                {_insertJob}
                ON CONFLICT (operation_id) DO NOTHING
                """, connection, transaction);
            AddInsertParameters(command, created, row, null);
            var inserted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return inserted;
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryRetryWithinCapacityAsync(
        ExecutionJobRecord job,
        int maximumOutstanding,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutstanding);
        if (job.Status != ExecutionJobStatus.Queued)
        {
            throw new ArgumentException("A retry must enter the queue.", nameof(job));
        }

        var row = ExecutionJobRow.From(job);
        return await _database.ExecuteAsync(async (connection, attempt, token) =>
        {
            if (attempt.EarlierAttemptMayHaveCommitted
                && await IsStoredAsync(connection, job with { Version = job.Version + 1 }, row, token).ConfigureAwait(false))
            {
                return true;
            }

            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await LockCapacityAsync(transaction, row.Kind, token).ConfigureAwait(false);
            if (await CountOutstandingAsync(transaction, row.Kind, token).ConfigureAwait(false) >= maximumOutstanding)
            {
                return false;
            }

            await using var command = new NpgsqlCommand($"""
                {_compareAndSet}
                AND kind = @kind AND status IN ('Failed', 'Cancelled')
                """, connection, transaction);
            AddAttemptStateParameters(command, job);
            row.AddRecordParameters(command);
            command.Parameters.AddWithValue("expected_version", NpgsqlDbType.Bigint, job.Version);
            command.Parameters.AddWithValue("retry_delay", NpgsqlDbType.Interval, RetryDelay(job));
            command.Parameters.Add(IsTerminalParameter(job));
            command.Parameters.Add(RetentionParameter(null));
            var updated = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return updated;
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
    }

    private async Task LockCapacityAsync(NpgsqlTransaction transaction, string kind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@capacity_key, 0))", transaction.Connection, transaction);
        command.Parameters.AddWithValue("capacity_key", $"{_jobs}:capacity:{kind}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> CountOutstandingAsync(NpgsqlTransaction transaction, string kind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM {_jobs}
            WHERE kind = @kind AND status IN ({ExecutionJobRow.ActiveStatusList})
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("kind", kind);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
