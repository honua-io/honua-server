// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.ControlPlane;

internal sealed class PostgresOperationInstanceStore : IOperationInstanceStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _instances;
    private readonly PostgresOperationLeaseStore _leases;

    public PostgresOperationInstanceStore(NpgsqlDataSource dataSource, string? schemaName = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _instances = SchemaSearchPath.QualifyTable("operation_instances", schemaName);
        _leases = new PostgresOperationLeaseStore(dataSource, "operation_instance_leases", schemaName);
    }

    public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return PersistAsync(envelope with { Version = 0 }, $"""
            INSERT INTO {_instances} (operation_instance_id, version, status, payload)
            VALUES (@id, @version, @status, @payload)
            ON CONFLICT (operation_instance_id) DO NOTHING
            """, cancellationToken);
    }

    public async Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var current = await GetAsync(envelope.OperationInstanceId, cancellationToken).ConfigureAwait(false);
        if (current is null || !await TrySetAsync(envelope, current.Version, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The accepted operation instance could not be updated.");
        }
    }

    public Task<bool> TrySetAsync(OperationHandle envelope, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return PersistAsync(envelope with { Version = checked(expectedVersion + 1) }, $"""
            UPDATE {_instances} SET version = @version, status = @status, payload = @payload
            WHERE operation_instance_id = @id AND version = @expected
            """, cancellationToken);
    }

    public async Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationInstanceId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT payload::text FROM {_instances} WHERE operation_instance_id = @id", connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Text, operationInstanceId);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is string json
            ? JsonSerializer.Deserialize(json, PostgresOperationInstanceJsonContext.Default.OperationHandle)
            : null;
    }

    public async Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT payload::text FROM {_instances} WHERE status = ANY(@active)", connection);
        command.Parameters.AddWithValue("active", NpgsqlDbType.Array | NpgsqlDbType.Integer, new[]
        {
            (int)OperationHandleStatus.Accepted, (int)OperationHandleStatus.Queued,
            (int)OperationHandleStatus.Running, (int)OperationHandleStatus.RequiresApproval
        });
        var active = new List<OperationHandle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            active.Add(JsonSerializer.Deserialize(reader.GetString(0), PostgresOperationInstanceJsonContext.Default.OperationHandle)
                ?? throw new InvalidOperationException("Stored operation instance payload is null."));
        }
        return active;
    }

    public Task<bool> TryAcquireLeaseAsync(string leaseId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
        => _leases.TryAcquireAsync(new PostgresOperationLease(leaseId, ownerId, duration), cancellationToken);

    public Task ReleaseLeaseAsync(string leaseId, string ownerId, CancellationToken cancellationToken = default)
        => _leases.ReleaseAsync(leaseId, ownerId, cancellationToken);

    private async Task<bool> PersistAsync(OperationHandle envelope, string statement, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.OperationInstanceId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(statement, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Text, envelope.OperationInstanceId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, envelope.Version);
        command.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, envelope.Version - 1);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Integer, (int)envelope.Status);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(envelope, PostgresOperationInstanceJsonContext.Default.OperationHandle));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
