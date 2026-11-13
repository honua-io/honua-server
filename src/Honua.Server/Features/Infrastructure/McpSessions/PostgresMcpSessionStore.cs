// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.ControlPlane;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.McpSessions;

// MCP sessions every node accepts until idle expiry, decided against the database clock. Removing
// a session removes its requests; other nodes see both on their next poll.
internal sealed partial class PostgresMcpSessionStore(JobStoreDatabase database) : IMcpSessionStore, IExpiredRowSweep
{
    // A touch that would extend the life by less than this is skipped, so a busy session writes at
    // most once a minute.
    private static readonly TimeSpan TouchGranularity = TimeSpan.FromMinutes(1);

    private readonly string _sessions = database.Table("mcp_sessions");
    private readonly string _requests = database.Table("mcp_requests");

    public Task SaveAsync(McpSessionRecord record, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_sessions} (session_id, principal_key, record, expires_at)
                    VALUES (@session_id, @principal_key, @record, now() + @idle)
                    ON CONFLICT (session_id) DO NOTHING
                    """,
                    connection);
                AddSessionId(command, record.SessionId);
                command.Parameters.Add(new NpgsqlParameter("principal_key", NpgsqlDbType.Text) { Value = record.PrincipalKey });
                command.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json)
                {
                    Value = JsonSerializer.Serialize(record, McpSessionStoreJsonContext.Default.McpSessionRecord),
                });
                command.Parameters.Add(new NpgsqlParameter("idle", NpgsqlDbType.Interval) { Value = idleTimeout });
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    public Task<McpSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT record::text FROM {_sessions} WHERE session_id = @session_id AND expires_at > now()",
                    connection);
                AddSessionId(command, sessionId);
                var payload = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                return ReadRecord(sessionId, payload);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    // Writes only when the remaining life is under the idle timeout less one minute, and reports
    // whether the session is live either way.
    public Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH touched AS (
                        UPDATE {_sessions} SET expires_at = now() + @idle
                        WHERE session_id = @session_id AND expires_at > now() AND expires_at < now() + @rewrite_below
                        RETURNING 1)
                    SELECT EXISTS (SELECT 1 FROM touched)
                        OR EXISTS (SELECT 1 FROM {_sessions} WHERE session_id = @session_id AND expires_at > now())
                    """,
                    connection);
                AddSessionId(command, sessionId);
                command.Parameters.Add(new NpgsqlParameter("idle", NpgsqlDbType.Interval) { Value = idleTimeout });
                command.Parameters.Add(new NpgsqlParameter("rewrite_below", NpgsqlDbType.Interval) { Value = RewriteBelow(idleTimeout) });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT EXISTS (SELECT 1 FROM {_sessions} WHERE session_id = @session_id AND expires_at > now())",
                    connection);
                AddSessionId(command, sessionId);
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand($"DELETE FROM {_sessions} WHERE session_id = @session_id", connection);
                AddSessionId(command, sessionId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    // Requests go with their session; a live session's expired requests go on its next register.
    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT session_id FROM {_sessions}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_sessions} AS session USING expired
                    WHERE session.session_id = expired.session_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    // An idle timeout of two minutes or less rewrites once half of it has passed instead.
    private static TimeSpan RewriteBelow(TimeSpan idleTimeout)
        => idleTimeout > 2 * TouchGranularity ? idleTimeout - TouchGranularity : idleTimeout / 2;

    private static McpSessionRecord? ReadRecord(string sessionId, string? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var record = JsonSerializer.Deserialize(payload, McpSessionStoreJsonContext.Default.McpSessionRecord);
        return record is not null && string.Equals(record.SessionId, sessionId, StringComparison.Ordinal) ? record : null;
    }

    private static void AddSessionId(NpgsqlCommand command, string sessionId)
        => command.Parameters.Add(new NpgsqlParameter("session_id", NpgsqlDbType.Text) { Value = sessionId });
}
