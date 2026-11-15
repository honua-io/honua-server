// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Events;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Streaming;

// A position only moves forward, so a late write from a consumer that lost its lease cannot rewind it.
internal sealed class PostgresStreamCursorStore(JobStoreDatabase database) : IStreamCursorStore
{
    private readonly string _cursors = database.Table("stream_cursors");

    public Task<long?> ReadAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return database.ExecuteAsync<long?>(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand($"SELECT position FROM {_cursors} WHERE name = @name", connection);
                command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
                var position = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return position is long stored ? stored : null;
            },
            StatementRepeat.SafeToRepeat,
            ct);
    }

    public Task WriteAsync(string name, long position, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_cursors} AS stream_cursors (name, position) VALUES (@name, @position)
                    ON CONFLICT (name) DO UPDATE
                    SET position = GREATEST(stream_cursors.position, EXCLUDED.position), updated_at = now()
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
                command.Parameters.Add(new NpgsqlParameter("position", NpgsqlDbType.Bigint) { Value = position });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            ct);
    }
}
