// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.ControlPlane;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.McpSessions;

// Requests running in a session. A request is live while its row and its session are; a cancel
// on any node sets the flag that the owning node's poll reads.
internal sealed partial class PostgresMcpSessionStore
{
    public Task<McpRequestState> UpdateRequestAsync(
        string sessionId, string principalKey, string requestKey, string generation,
        McpRequestOperation operation, CancellationToken cancellationToken)
    {
        var request = new McpActiveRequest(sessionId, principalKey, requestKey, generation);
        return operation switch
        {
            McpRequestOperation.Register => RegisterAsync(request, cancellationToken),
            McpRequestOperation.Read => ReadAsync(request, cancellationToken),
            McpRequestOperation.Cancel => CancelAsync(request, cancellationToken),
            McpRequestOperation.Finish => FinishAsync(request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    public Task<McpPollResult> PollAsync(McpPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionIds.Count == 0 && request.Requests.Count == 0)
        {
            return Task.FromResult(McpPollResult.Nothing);
        }

        return database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(PollStatement(), connection);
                AddPollParameters(command, request);
                return await ReadPollResultAsync(command, request, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    // Set-if-absent under the session and principal caps, in one call of the register function.
    private Task<McpRequestState> RegisterAsync(McpActiveRequest request, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT {database.Table("mcp_register_request")}(
                        @session_id, @principal_key, @request_key, @generation, @lifetime, @per_session, @per_principal)
                    """,
                    connection);
                AddRequest(command, request);
                command.Parameters.Add(new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = McpRequestBounds.Lifetime });
                command.Parameters.Add(new NpgsqlParameter("per_session", NpgsqlDbType.Integer) { Value = McpRequestBounds.PerSession });
                command.Parameters.Add(new NpgsqlParameter("per_principal", NpgsqlDbType.Integer) { Value = McpRequestBounds.PerPrincipal });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) switch
                {
                    "active" => McpRequestState.Active,
                    "duplicate" => McpRequestState.Duplicate,
                    "capacity" => McpRequestState.Capacity,
                    _ => McpRequestState.Missing,
                };
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    private Task<McpRequestState> ReadAsync(McpActiveRequest request, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT request.cancelled FROM {_requests} AS request
                    JOIN {_sessions} AS session ON session.session_id = request.session_id
                    WHERE request.session_id = @session_id AND request.request_key = @request_key
                        AND request.generation = @generation AND request.principal_key = @principal_key
                        AND request.expires_at > now() AND session.expires_at > now()
                    """,
                    connection);
                AddRequest(command, request);
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) switch
                {
                    true => McpRequestState.Cancelled,
                    false => McpRequestState.Active,
                    _ => McpRequestState.Missing,
                };
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    // Any generation of the request id: the caller names the request, not the run of it.
    private Task<McpRequestState> CancelAsync(McpActiveRequest request, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {_requests} AS request SET cancelled = true
                    FROM {_sessions} AS session
                    WHERE request.session_id = @session_id AND request.request_key = @request_key
                        AND request.principal_key = @principal_key AND request.expires_at > now()
                        AND session.session_id = request.session_id AND session.expires_at > now()
                    """,
                    connection);
                AddRequest(command, request);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1
                    ? McpRequestState.Cancelled
                    : McpRequestState.Missing;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    // Removes this generation only, so a delayed finish never removes a later run of the same id.
    private Task<McpRequestState> FinishAsync(McpActiveRequest request, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH finished AS (
                        DELETE FROM {_requests}
                        WHERE session_id = @session_id AND request_key = @request_key
                            AND generation = @generation AND principal_key = @principal_key
                        RETURNING cancelled, expires_at > now() AS live)
                    SELECT finished.cancelled FROM finished
                    WHERE finished.live
                        AND EXISTS (SELECT 1 FROM {_sessions} WHERE session_id = @session_id AND expires_at > now())
                    """,
                    connection);
                AddRequest(command, request);
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) switch
                {
                    true => McpRequestState.Cancelled,
                    false => McpRequestState.Active,
                    _ => McpRequestState.Missing,
                };
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    // Watched sessions that are gone, and running requests that are cancelled or gone, in one query.
    private string PollStatement() =>
        $"""
        SELECT watched.session_id, NULL::text
        FROM unnest(@session_ids) AS watched(session_id)
        WHERE NOT EXISTS (
            SELECT 1 FROM {_sessions} AS session
            WHERE session.session_id = watched.session_id AND session.expires_at > now())
        UNION ALL
        SELECT running.session_id, running.generation
        FROM unnest(@request_session_ids, @request_keys, @generations) AS running(session_id, request_key, generation)
        WHERE NOT EXISTS (
            SELECT 1 FROM {_requests} AS request
            JOIN {_sessions} AS session ON session.session_id = request.session_id
            WHERE request.session_id = running.session_id AND request.request_key = running.request_key
                AND request.generation = running.generation AND NOT request.cancelled
                AND request.expires_at > now() AND session.expires_at > now())
        """;

    private static void AddPollParameters(NpgsqlCommand command, McpPollRequest request)
    {
        AddTextArray(command, "session_ids", request.SessionIds);
        AddTextArray(command, "request_session_ids", request.Requests.Select(static running => running.SessionId));
        AddTextArray(command, "request_keys", request.Requests.Select(static running => running.RequestKey));
        AddTextArray(command, "generations", request.Requests.Select(static running => running.Generation));
    }

    // A generation is unique to one run, so it names the ended request without its other fields.
    private static async Task<McpPollResult> ReadPollResultAsync(
        NpgsqlCommand command, McpPollRequest request, CancellationToken cancellationToken)
    {
        var endedSessions = new List<string>();
        var endedGenerations = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1))
            {
                endedSessions.Add(reader.GetString(0));
            }
            else
            {
                endedGenerations.Add(reader.GetString(1));
            }
        }

        return new McpPollResult(
            endedSessions,
            request.Requests.Where(running => endedGenerations.Contains(running.Generation)).ToArray());
    }

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string> values)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    private static void AddRequest(NpgsqlCommand command, McpActiveRequest request)
    {
        AddSessionId(command, request.SessionId);
        command.Parameters.Add(new NpgsqlParameter("principal_key", NpgsqlDbType.Text) { Value = request.PrincipalKey });
        command.Parameters.Add(new NpgsqlParameter("request_key", NpgsqlDbType.Text) { Value = request.RequestKey });
        command.Parameters.Add(new NpgsqlParameter("generation", NpgsqlDbType.Text) { Value = request.Generation });
    }
}
