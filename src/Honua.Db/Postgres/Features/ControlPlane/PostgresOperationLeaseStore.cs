// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.ControlPlane;

internal readonly record struct PostgresOperationLease(string Id, string Owner, TimeSpan Duration);

internal sealed class PostgresOperationLeaseStore(NpgsqlDataSource dataSource, string tableName, string? schemaName)
{
    private readonly string _table = SchemaSearchPath.QualifyTable(tableName, schemaName);

    public async Task<bool> TryAcquireAsync(PostgresOperationLease lease, CancellationToken cancellationToken)
    {
        Validate(lease);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_table} AS held (operation_id, owner_id, expires_at)
            VALUES (@id, @owner, clock_timestamp() + @duration)
            ON CONFLICT (operation_id) DO UPDATE
            SET owner_id = EXCLUDED.owner_id, expires_at = clock_timestamp() + @duration
            WHERE held.expires_at <= clock_timestamp()
            """, connection);
        AddParameters(command, lease.Id, lease.Owner);
        command.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, lease.Duration);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RenewAsync(PostgresOperationLease lease, CancellationToken cancellationToken)
    {
        Validate(lease);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_table} SET expires_at = clock_timestamp() + @duration
            WHERE operation_id = @id AND owner_id = @owner AND expires_at > clock_timestamp()
            """, connection);
        AddParameters(command, lease.Id, lease.Owner);
        command.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, lease.Duration);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task ReleaseAsync(string operationId, string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"DELETE FROM {_table} WHERE operation_id = @id AND owner_id = @owner", connection);
        AddParameters(command, operationId, ownerId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(PostgresOperationLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.Owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease.Duration, TimeSpan.Zero);
    }

    private static void AddParameters(NpgsqlCommand command, string operationId, string ownerId)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Text, operationId);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Text, ownerId);
    }
}
