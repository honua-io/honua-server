// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ObjectStorage;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Security;

// Every read goes to PostgreSQL, so all tasks see the same connections and the same setup state.
internal sealed class PostgresObjectStorageConnectionStore : IObjectStorageConnectionStore
{
    private const string Columns =
        "connection_id, name, description, provider, bucket, prefix, region, role_arn, external_id, created_by, created_at, updated_at";

    // Open (unexpired, unconsumed) setups an installation may hold at once; expired ones are pruned on every save.
    internal const int MaxOpenSetups = 100;

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _connections;
    private readonly string _setups;

    public PostgresObjectStorageConnectionStore(NpgsqlDataSource dataSource, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _connections = SchemaSearchPath.QualifyTable("object_storage_connections", schemaName);
        _setups = SchemaSearchPath.QualifyTable("object_storage_connection_setups", schemaName);
    }

    public async Task<bool> SaveSetupAsync(ObjectStorageConnectionSetup setup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var sql = $"""
            WITH pruned AS (
                DELETE FROM {_setups} WHERE consumed_at IS NULL AND expires_at <= clock_timestamp()
            )
            INSERT INTO {_setups} (external_id, bucket, prefix, expires_at)
            SELECT @external_id, @bucket, @prefix, @expires_at
            WHERE (SELECT count(*) FROM {_setups} WHERE consumed_at IS NULL AND expires_at > clock_timestamp()) < @max_open;
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("external_id", NpgsqlDbType.Text, setup.ExternalId);
        command.Parameters.AddWithValue("bucket", NpgsqlDbType.Text, setup.Bucket);
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Text, setup.Prefix);
        command.Parameters.AddWithValue("expires_at", NpgsqlDbType.TimestampTz, setup.ExpiresAt);
        command.Parameters.AddWithValue("max_open", NpgsqlDbType.Bigint, (long)MaxOpenSetups);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<ObjectStorageConnectionSetup?> FindOpenSetupAsync(string externalId, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT external_id, bucket, prefix, expires_at FROM {_setups}
            WHERE external_id = @external_id AND consumed_at IS NULL AND expires_at > clock_timestamp()
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("external_id", NpgsqlDbType.Text, externalId ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ObjectStorageConnectionSetup
        {
            ExternalId = reader.GetString(0),
            Bucket = reader.GetString(1),
            Prefix = reader.GetString(2),
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(3),
        };
    }

    public async Task<ObjectStorageCreateOutcome> CreateAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var db = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await ConsumeSetupAsync(transaction, connection, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ObjectStorageCreateOutcome.SetupRejected;
        }

        var sql = $"""
            INSERT INTO {_connections} ({Columns})
            VALUES (@id, @name, @description, @provider, @bucket, @prefix, @region, @role_arn, @external_id,
                    @created_by, clock_timestamp(), clock_timestamp())
            """;
        await using (var insert = new NpgsqlCommand(sql, db, transaction))
        {
            AddConnectionParameters(insert, connection);
            insert.Parameters.AddWithValue("provider", NpgsqlDbType.Text, connection.Provider);
            insert.Parameters.AddWithValue("external_id", NpgsqlDbType.Text, connection.ExternalId ?? string.Empty);
            insert.Parameters.AddWithValue("created_by", NpgsqlDbType.Text, connection.CreatedBy);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return ObjectStorageCreateOutcome.Created;
    }

    public async Task<ObjectStorageConnection?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand($"SELECT {Columns} FROM {_connections} WHERE connection_id = @id");
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand($"SELECT {Columns} FROM {_connections} ORDER BY name, connection_id");
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var sql = $"""
            UPDATE {_connections}
            SET name = @name, description = @description, region = @region, role_arn = @role_arn, updated_at = clock_timestamp()
            WHERE connection_id = @id AND bucket = @bucket AND prefix = @prefix
            """;
        await using var command = _dataSource.CreateCommand(sql);
        AddConnectionParameters(command, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand($"DELETE FROM {_connections} WHERE connection_id = @id");
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    // Single use: only an unexpired, unconsumed setup minted for the same bucket and prefix is accepted.
    private async Task<bool> ConsumeSetupAsync(
        NpgsqlTransaction transaction,
        ObjectStorageConnection connection,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {_setups} SET consumed_at = clock_timestamp()
            WHERE external_id = @external_id AND bucket = @bucket AND prefix = @prefix
              AND consumed_at IS NULL AND expires_at > clock_timestamp()
            """;
        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("external_id", NpgsqlDbType.Text, connection.ExternalId ?? string.Empty);
        command.Parameters.AddWithValue("bucket", NpgsqlDbType.Text, connection.Bucket);
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Text, connection.Prefix);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddConnectionParameters(NpgsqlCommand command, ObjectStorageConnection connection)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, connection.Id);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Text, connection.Name);
        command.Parameters.AddWithValue("description", NpgsqlDbType.Text, (object?)connection.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("bucket", NpgsqlDbType.Text, connection.Bucket);
        command.Parameters.AddWithValue("prefix", NpgsqlDbType.Text, connection.Prefix);
        command.Parameters.AddWithValue("region", NpgsqlDbType.Text, connection.Region);
        command.Parameters.AddWithValue("role_arn", NpgsqlDbType.Text, connection.RoleArn ?? string.Empty);
    }

    private static async Task<IReadOnlyList<ObjectStorageConnection>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<ObjectStorageConnection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ObjectStorageConnection
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Provider = reader.GetString(3),
                Bucket = reader.GetString(4),
                Prefix = reader.GetString(5),
                Region = reader.GetString(6),
                RoleArn = reader.GetString(7),
                ExternalId = reader.GetString(8),
                CreatedBy = reader.GetString(9),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(10),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(11),
            });
        }

        return rows;
    }
}
