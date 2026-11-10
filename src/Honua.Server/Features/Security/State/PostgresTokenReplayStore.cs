// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Security.State;

internal sealed class PostgresTokenReplayStore(NpgsqlDataSource dataSource, JobStoreDatabase database)
    : ITokenReplayStore, IExpiredRowSweep
{
    private readonly string _register = $"""
        INSERT INTO {database.Table("token_replay")} AS presented (replay_key, marker, expires_at)
        VALUES (@replay_key, @marker, now() + @lifetime)
        ON CONFLICT (replay_key) DO UPDATE SET marker = EXCLUDED.marker, expires_at = EXCLUDED.expires_at
        WHERE presented.expires_at <= now()
        """;

    private readonly string _replays = database.Table("token_replay");

    public async Task<TokenReplayRegistrationOutcome> TryRegisterAsync(
        TokenReplayRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var registered = await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(_register, connection);
                AddRegistrationParameters(command, request);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken).ConfigureAwait(false);
        if (registered)
        {
            return new TokenReplayRegistrationOutcome(Registered: true, ExistingMarker: null);
        }

        var existingMarker = await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT marker FROM {_replays} WHERE replay_key = @replay_key AND expires_at > now()",
                    connection);
                command.Parameters.Add(new NpgsqlParameter("replay_key", NpgsqlDbType.Text) { Value = request.ReplayKey });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
        return new TokenReplayRegistrationOutcome(Registered: false, existingMarker);
    }

    public Task<bool> TryRebindAsync(TokenReplayRebinding rebinding, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {_replays} SET marker = @to_marker
                    WHERE replay_key = @replay_key AND marker IN (@from_marker, @to_marker) AND expires_at > now()
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("replay_key", NpgsqlDbType.Text) { Value = rebinding.ReplayKey });
                command.Parameters.Add(new NpgsqlParameter("from_marker", NpgsqlDbType.Text) { Value = rebinding.FromMarker });
                command.Parameters.Add(new NpgsqlParameter("to_marker", NpgsqlDbType.Text) { Value = rebinding.ToMarker });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public bool TryRegister(TokenReplayRegistrationRequest request)
    {
        using var command = dataSource.CreateCommand(_register);
        AddRegistrationParameters(command, request);
        return command.ExecuteNonQuery() == 1;
    }

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT replay_key FROM {_replays}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_replays} AS presented USING expired WHERE presented.replay_key = expired.replay_key
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static void AddRegistrationParameters(NpgsqlCommand command, TokenReplayRegistrationRequest request)
    {
        command.Parameters.Add(new NpgsqlParameter("replay_key", NpgsqlDbType.Text) { Value = request.ReplayKey });
        command.Parameters.Add(new NpgsqlParameter("marker", NpgsqlDbType.Text) { Value = request.Marker });
        command.Parameters.Add(new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = request.Lifetime });
    }
}
