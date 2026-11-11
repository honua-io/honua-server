// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Operations;

internal sealed class PostgresOperationInstanceStore(
    JobStoreDatabase database,
    ILeaseStore leases,
    IOptions<ControlPlaneRetentionOptions> retention) : IOperationInstanceStore, IExpiredRowSweep
{
    private const string LeaseKeyPrefix = "operation-instance:";
    private const string RecordAssignments = "active = @active, expires_at = now() + @retention, record = @record";

    private readonly string _instances = database.Table("operation_instances");

    public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        => WriteAsync(
            $"""
            INSERT INTO {_instances} (operation_instance_id, version, active, expires_at, record)
            VALUES (@operation_instance_id, @version, @active, now() + @retention, @record)
            ON CONFLICT (operation_instance_id) DO NOTHING
            """,
            envelope with { Version = 0 },
            expectedVersion: null,
            cancellationToken);

    public async Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        var updated = await WriteAsync(
            $"""
            UPDATE {_instances} SET version = version + 1, {RecordAssignments}
            WHERE operation_instance_id = @operation_instance_id
            """,
            envelope,
            expectedVersion: null,
            cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            throw new InvalidOperationException($"Operation instance '{envelope.OperationInstanceId}' was not durably accepted.");
        }
    }

    public Task<bool> TrySetAsync(
        OperationHandle envelope,
        long expectedVersion,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            $"""
            UPDATE {_instances} SET version = @version, {RecordAssignments}
            WHERE operation_instance_id = @operation_instance_id AND version = @expected_version
            """,
            envelope with { Version = expectedVersion + 1 },
            expectedVersion,
            cancellationToken);

    public async Task<OperationHandle?> GetAsync(
        string operationInstanceId,
        CancellationToken cancellationToken = default)
    {
        var found = await ReadAsync(
            $"SELECT version, record FROM {_instances} WHERE operation_instance_id = @operation_instance_id",
            operationInstanceId,
            cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    public async Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
        => await ReadAsync(
            $"SELECT version, record FROM {_instances} WHERE active",
            operationInstanceId: null,
            cancellationToken).ConfigureAwait(false);

    public Task<bool> TryAcquireLeaseAsync(
        string leaseId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
        => leases.TryAcquireAsync(new LeaseClaim(LeaseKeyPrefix + leaseId, ownerId), duration, cancellationToken);

    public Task ReleaseLeaseAsync(
        string leaseId,
        string ownerId,
        CancellationToken cancellationToken = default)
        => leases.ReleaseAsync(new LeaseClaim(LeaseKeyPrefix + leaseId, ownerId), cancellationToken);

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT operation_instance_id FROM {_instances}
                        WHERE NOT active AND expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_instances} AS instance USING expired
                    WHERE instance.operation_instance_id = expired.operation_instance_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private Task<bool> WriteAsync(
        string statement,
        OperationHandle envelope,
        long? expectedVersion,
        CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                command.Parameters.Add(new NpgsqlParameter("operation_instance_id", NpgsqlDbType.Text) { Value = envelope.OperationInstanceId });
                command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = envelope.Version });
                command.Parameters.Add(new NpgsqlParameter("active", NpgsqlDbType.Boolean) { Value = IsActive(envelope) });
                command.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = retention.Value.OperationInstances });
                command.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json)
                {
                    Value = JsonSerializer.Serialize(envelope, OperationsJsonContext.Default.OperationHandle)
                });
                if (expectedVersion.HasValue)
                {
                    command.Parameters.Add(new NpgsqlParameter("expected_version", NpgsqlDbType.Bigint) { Value = expectedVersion.Value });
                }

                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    private Task<List<OperationHandle>> ReadAsync(
        string statement,
        string? operationInstanceId,
        CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                if (operationInstanceId is not null)
                {
                    command.Parameters.Add(new NpgsqlParameter("operation_instance_id", NpgsqlDbType.Text) { Value = operationInstanceId });
                }

                var handles = new List<OperationHandle>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var handle = JsonSerializer.Deserialize(reader.GetString(1), OperationsJsonContext.Default.OperationHandle)
                        ?? throw new InvalidDataException("A stored operation instance record is empty.");
                    handles.Add(handle with { Version = reader.GetInt64(0) });
                }

                return handles;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static bool IsActive(OperationHandle envelope)
        => envelope.Status is OperationHandleStatus.Accepted
            or OperationHandleStatus.Queued
            or OperationHandleStatus.Running
            or OperationHandleStatus.RequiresApproval;
}
