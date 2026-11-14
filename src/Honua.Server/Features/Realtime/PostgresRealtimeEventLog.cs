// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;
using Honua.ControlPlane;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Realtime;

// One event to append: the scope readers filter on, the event kind and its JSON payload.
internal readonly record struct RealtimeLogRow(string ScopeKey, string Kind, JsonElement Payload);

// The realtime event log in the system database (ADR-0085). Appends go through realtime_append, the
// function triggers call too, so sequence allocation and the notification live in one place.
internal sealed class PostgresRealtimeEventLog(JobStoreDatabase database) : IExpiredRowSweep
{
    // The largest payload the log accepts, measured by realtime_append on the stored text; the
    // function is the only judge, so writers and triggers are held to the same bound.
    public const int MaxPayloadBytes = 64 * 1024;

    // Scope keys are ids (a map, a datastream, a job); this keeps them well inside a btree entry.
    public const int MaxScopeKeyBytes = 512;

    private readonly string _heads = database.Table("realtime_log_heads");
    private readonly string _events = database.Table("realtime_events");
    private readonly string _append = database.Table("realtime_append");

    // Appends rows to family in order and returns the family's new head. With a transaction the
    // append commits with it; it holds the family's head row to commit, so append last. Without one
    // the append is never sent twice, because a resend would duplicate events. A payload over the
    // limit is refused by the database with ArgumentException; on the transactional path that
    // leaves the caller's transaction aborted, as for any failed statement.
    public Task<long> AppendAsync(
        string family,
        IReadOnlyList<RealtimeLogRow> rows,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(rows);
        var request = new AppendRequest(family, SerializeRows(rows), transaction);

        return transaction is not null
            ? AppendOnAsync(transaction.Connection!, request, cancellationToken)
            : database.ExecuteAsync(
                (connection, _, token) => AppendOnAsync(connection, request, token),
                StatementRepeat.NotAfterSend,
                cancellationToken);
    }

    // Writes each family's retention where it differs, so tasks that agree write nothing.
    public Task<int> ApplyRetentionAsync(IReadOnlyDictionary<string, TimeSpan> retention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        if (retention.Count == 0)
        {
            return Task.FromResult(0);
        }

        var families = retention.Keys.ToArray();
        var intervals = families.Select(family => retention[family]).ToArray();
        return database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {_heads} AS heads SET retention = wanted.retention
                    FROM unnest(@families, @retentions) AS wanted (family, retention)
                    WHERE heads.family = wanted.family AND heads.retention IS DISTINCT FROM wanted.retention
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("families", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = families });
                command.Parameters.Add(new NpgsqlParameter("retentions", NpgsqlDbType.Array | NpgsqlDbType.Interval) { Value = intervals });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    // Deletes a prefix of each family, oldest first, up to an expired row taken from one batch of the
    // oldest expired rows, so each call does bounded work. A row that has not expired but is older
    // than an expired one goes too, so the log has no hole and the oldest retained sequence tells a
    // reader exactly what it missed. Rows are locked without SKIP LOCKED: a second node's sweep waits
    // for the first instead of deleting a later range ahead of it.
    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH oldest AS (
                        SELECT family, seq FROM {_events}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size),
                    cut AS (
                        SELECT family, max(seq) AS through FROM oldest GROUP BY family),
                    expired AS (
                        SELECT logged.family, logged.seq FROM {_events} AS logged
                        JOIN cut ON cut.family = logged.family AND logged.seq <= cut.through
                        ORDER BY logged.family, logged.seq LIMIT @batch_size
                        FOR UPDATE OF logged)
                    DELETE FROM {_events} AS logged USING expired
                    WHERE logged.family = expired.family AND logged.seq = expired.seq
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private async Task<long> AppendOnAsync(
        NpgsqlConnection connection,
        AppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {_append}(@family, @rows)", connection, request.Transaction);
        command.Parameters.Add(new NpgsqlParameter("family", NpgsqlDbType.Text) { Value = request.Family });
        command.Parameters.Add(new NpgsqlParameter("rows", NpgsqlDbType.Jsonb) { Value = request.Batch });
        try
        {
            var head = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return (long)head!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ProgramLimitExceeded
                                                  && exception.MessageText.StartsWith("realtime_append:", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A {request.Family} event has a payload over {MaxPayloadBytes} bytes; realtime events carry references, not documents.",
                nameof(request),
                exception);
        }
    }

    private readonly record struct AppendRequest(string Family, string Batch, NpgsqlTransaction? Transaction);

    private static string SerializeRows(IReadOnlyList<RealtimeLogRow> rows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                ArgumentException.ThrowIfNullOrEmpty(row.ScopeKey, nameof(rows));
                if (System.Text.Encoding.UTF8.GetByteCount(row.ScopeKey) > MaxScopeKeyBytes)
                {
                    throw new ArgumentException($"A realtime scope key is limited to {MaxScopeKeyBytes} bytes.", nameof(rows));
                }

                ArgumentException.ThrowIfNullOrEmpty(row.Kind, nameof(rows));
                writer.WriteStartObject();
                writer.WriteString("scope_key", row.ScopeKey);
                writer.WriteString("kind", row.Kind);
                writer.WritePropertyName("payload");
                row.Payload.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
