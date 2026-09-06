// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed class PostgresQuerySnapshotStore(IAdoNetDatabaseConnectionProvider connectionProvider) : IQuerySnapshotStore
{
    public async Task SaveAsync(Guid id, byte[] payload, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        // A bounded cleanup batch avoids turning a normal poll into a retention sweep.
        const string cleanupSql = """
            DELETE FROM honua.query_snapshots WHERE id IN (
                SELECT id FROM honua.query_snapshots WHERE expires_at <= CURRENT_TIMESTAMP
                ORDER BY expires_at LIMIT 100);
            """;
        await using var batch = new NpgsqlBatch(connection);
        batch.BatchCommands.Add(new NpgsqlBatchCommand(cleanupSql));
        var command = new NpgsqlBatchCommand("INSERT INTO honua.query_snapshots(id, payload, expires_at) VALUES ($1, $2, $3)");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = payload });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = expiresAt });
        batch.BatchCommands.Add(command);
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT payload FROM honua.query_snapshots WHERE id = $1 AND expires_at > CURRENT_TIMESTAMP";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
    }
}
