// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.SecurityState;

// One row per source. A window is over when expires_at has passed; a refusal in force ignores further failures,
// matching the per-instance limiter. Times come from the caller's clock so every instance judges windows alike.
internal sealed class PostgresAuthenticationFailureLedger(JobStoreDatabase database, IOptions<AuthenticationFailureLimiterOptions> options)
    : IAuthenticationFailureLedger, IExpiredRowSweep
{
    private const string Refused = "stored.refused_until > @now";
    private const string Over = "stored.expires_at <= @now";
    private const string NextFailures = $"(CASE WHEN {Over} THEN 1 ELSE stored.failures + 1 END)";
    private const string ReachesThreshold = $"{NextFailures} >= @threshold";

    private readonly string _windows = database.Table("authentication_failure_windows");

    public Task<SharedFailureWindow> RecordFailureAsync(AuthenticationFailure failure, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_windows} AS stored (source_digest, window_started, failures, refused_until, expires_at)
                    VALUES (@digest, @now, 1,
                        CASE WHEN @threshold <= 1 THEN @now + @block END,
                        CASE WHEN @threshold <= 1 THEN @now + @block ELSE @now + @window END)
                    ON CONFLICT (source_digest) DO UPDATE SET
                        window_started = CASE WHEN {Refused} THEN stored.window_started WHEN {Over} THEN @now ELSE stored.window_started END,
                        failures = CASE WHEN {Refused} THEN stored.failures ELSE {NextFailures} END,
                        refused_until = CASE WHEN {Refused} THEN stored.refused_until WHEN {ReachesThreshold} THEN @now + @block END,
                        expires_at = CASE
                            WHEN {Refused} THEN stored.expires_at
                            WHEN {ReachesThreshold} THEN @now + @block
                            WHEN {Over} THEN @now + @window
                            ELSE stored.expires_at END
                    RETURNING failures, refused_until, expires_at
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("digest", NpgsqlDbType.Bytea) { Value = failure.SourceDigest });
                command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = failure.At.ToUniversalTime() });
                command.Parameters.Add(new NpgsqlParameter("threshold", NpgsqlDbType.Integer) { Value = failure.Policy.Threshold });
                command.Parameters.Add(new NpgsqlParameter("window", NpgsqlDbType.Interval) { Value = failure.Policy.Window });
                command.Parameters.Add(new NpgsqlParameter("block", NpgsqlDbType.Interval) { Value = failure.Policy.BlockDuration });
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await reader.ReadAsync(token).ConfigureAwait(false);
                return ReadWindow(reader, 0);
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    public Task<IReadOnlyList<KeyValuePair<byte[], SharedFailureWindow>>> ReadRefusalsAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
        => database.ExecuteAsync<IReadOnlyList<KeyValuePair<byte[], SharedFailureWindow>>>(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT source_digest, failures, refused_until, expires_at FROM {_windows} AS stored
                    WHERE {Refused} ORDER BY refused_until DESC LIMIT @limit
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUniversalTime() });
                command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var refusals = new List<KeyValuePair<byte[], SharedFailureWindow>>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    refusals.Add(new(reader.GetFieldValue<byte[]>(0), ReadWindow(reader, 1)));
                }

                return refusals;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    // Expired windows go first; beyond MaxTrackedSources the oldest unrefused windows go.
    // Match on the selected row version so a concurrent renewal is left for the next sweep.
    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT ctid FROM {_windows}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED),
                    overflow AS (
                        SELECT ctid FROM {_windows}
                        WHERE expires_at > now()
                        ORDER BY refused_until > now() DESC NULLS LAST, window_started DESC
                        OFFSET @max_sources LIMIT @batch_size),
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

    private static SharedFailureWindow ReadWindow(NpgsqlDataReader reader, int first)
        => new(
            reader.GetInt32(first),
            reader.IsDBNull(first + 1) ? null : reader.GetFieldValue<DateTimeOffset>(first + 1),
            reader.GetFieldValue<DateTimeOffset>(first + 2));
}
