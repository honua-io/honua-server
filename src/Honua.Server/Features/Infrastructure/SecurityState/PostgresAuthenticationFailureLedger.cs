// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.SecurityState;

// One row per source. A window is over when expires_at has passed; a refusal in force ignores further failures,
// matching the per-instance limiter. Writes and retention use the same database clock.
internal sealed class PostgresAuthenticationFailureLedger(JobStoreDatabase database, IOptions<AuthenticationFailureLimiterOptions> options)
    : IAuthenticationFailureLedger, IExpiredRowSweep
{
    private const string WriteTime = "statement_timestamp()";
    private const string RefusedAtWrite = $"stored.refused_until > {WriteTime}";
    private const string Over = $"stored.expires_at <= {WriteTime}";
    private const string NextFailures = $"(CASE WHEN {Over} THEN 1 ELSE stored.failures + 1 END)";
    private const string ReachesThreshold = $"{NextFailures} >= @threshold";

    private readonly string _windows = database.Table("authentication_failure_windows");

    public Task<LedgerFailureWindow?> RecordFailureAsync(AuthenticationFailure failure, CancellationToken cancellationToken)
        => database.ExecuteAsync<LedgerFailureWindow?>(
            async (connection, _, token) =>
            {
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                // Serialize changes to membership so separate writers cannot claim the last slot together.
                await using var capacityLock = new NpgsqlCommand(
                    $"LOCK TABLE {_windows} IN SHARE ROW EXCLUSIVE MODE", connection, transaction);
                await capacityLock.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await using var command = new NpgsqlCommand(WriteSql(), connection, transaction);
                command.Parameters.Add(new NpgsqlParameter("digest", NpgsqlDbType.Bytea) { Value = failure.SourceDigest });
                command.Parameters.Add(new NpgsqlParameter("max_sources", NpgsqlDbType.Integer) { Value = options.Value.MaxTrackedSources });
                command.Parameters.Add(new NpgsqlParameter("threshold", NpgsqlDbType.Integer) { Value = failure.Policy.Threshold });
                command.Parameters.Add(new NpgsqlParameter("window", NpgsqlDbType.Interval) { Value = failure.Policy.Window });
                command.Parameters.Add(new NpgsqlParameter("block", NpgsqlDbType.Interval) { Value = failure.Policy.BlockDuration });
                LedgerFailureWindow? result;
                await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    result = await reader.ReadAsync(token).ConfigureAwait(false) ? ReadWindow(reader, 0) : null;
                }
                await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
                return result;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    private string WriteSql() =>
        $"""
        WITH current_source AS (
            SELECT 1 FROM {_windows} WHERE source_digest = @digest),
        source_count AS (
            SELECT count(*) AS total FROM (
                SELECT 1 FROM {_windows} WHERE NOT EXISTS (SELECT 1 FROM current_source)
                LIMIT (@max_sources::bigint + 1)
            ) AS occupied),
        expired_slot AS (
            SELECT source_digest FROM {_windows}
            WHERE expires_at <= {WriteTime} AND NOT EXISTS (SELECT 1 FROM current_source)
                AND (SELECT total FROM source_count) = @max_sources
            ORDER BY expires_at, source_digest LIMIT 1),
        freed_slot AS (
            DELETE FROM {_windows} AS expired USING expired_slot
            WHERE expired.source_digest = expired_slot.source_digest
            RETURNING 1)
        INSERT INTO {_windows} AS stored (source_digest, window_started, failures, refused_until, expires_at)
        SELECT @digest, {WriteTime}, 1,
            CASE WHEN @threshold <= 1 THEN {WriteTime} + @block END,
            CASE WHEN @threshold <= 1 THEN {WriteTime} + @block ELSE {WriteTime} + @window END
        WHERE EXISTS (SELECT 1 FROM current_source)
            OR (SELECT total FROM source_count) < @max_sources
            OR EXISTS (SELECT 1 FROM freed_slot)
        ON CONFLICT (source_digest) DO UPDATE SET
            window_started = CASE WHEN {Over} THEN {WriteTime} ELSE stored.window_started END,
            failures = CASE WHEN {RefusedAtWrite} THEN stored.failures ELSE {NextFailures} END,
            refused_until = CASE
                WHEN {RefusedAtWrite} THEN stored.refused_until
                WHEN {ReachesThreshold} THEN GREATEST({WriteTime}, stored.window_started) + @block END,
            expires_at = CASE
                WHEN {RefusedAtWrite} THEN stored.expires_at
                WHEN {ReachesThreshold} THEN GREATEST({WriteTime}, stored.window_started) + @block
                WHEN {Over} THEN {WriteTime} + @window
                ELSE stored.expires_at END
        RETURNING failures, refused_until - {WriteTime}, expires_at - {WriteTime}
        """;

    public Task<IReadOnlyList<KeyValuePair<byte[], LedgerFailureWindow>>> ReadRefusalsAsync(CancellationToken cancellationToken)
        => database.ExecuteAsync<IReadOnlyList<KeyValuePair<byte[], LedgerFailureWindow>>>(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT source_digest, failures, refused_until - {WriteTime}, expires_at - {WriteTime}
                    FROM {_windows} WHERE refused_until > {WriteTime}
                    """,
                    connection);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var refusals = new List<KeyValuePair<byte[], LedgerFailureWindow>>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    refusals.Add(new(reader.GetFieldValue<byte[]>(0), ReadWindow(reader, 1)));
                }

                return refusals;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    // Expired windows go first, then the oldest unrefused windows beyond the retention target.
    // Match on the selected row version so a concurrent renewal is left for the next sweep.
    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT ctid FROM {_windows}
                        WHERE expires_at <= {WriteTime}
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED),
                    overflow AS (
                        SELECT ctid FROM {_windows}
                        WHERE expires_at > {WriteTime} AND (refused_until IS NULL OR refused_until <= {WriteTime})
                        ORDER BY window_started DESC, source_digest
                        OFFSET GREATEST(0, @max_sources - (
                            SELECT count(*) FROM {_windows} WHERE refused_until > {WriteTime}))
                        LIMIT (@batch_size - (SELECT count(*) FROM expired))),
                    doomed AS (
                        SELECT ctid FROM expired
                        UNION ALL
                        SELECT ctid FROM overflow)
                    DELETE FROM {_windows} AS stored USING doomed
                    WHERE stored.ctid = doomed.ctid
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                command.Parameters.Add(new NpgsqlParameter("max_sources", NpgsqlDbType.Integer) { Value = options.Value.MaxTrackedSources });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static LedgerFailureWindow ReadWindow(NpgsqlDataReader reader, int first)
        => new(
            reader.GetInt32(first),
            reader.IsDBNull(first + 1) ? null : reader.GetFieldValue<TimeSpan>(first + 1),
            reader.GetFieldValue<TimeSpan>(first + 2));
}
