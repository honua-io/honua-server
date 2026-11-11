// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Migration;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.Progress;

// An operation whose snapshot has not expired is listed as active whatever its status; callers
// filter by status, as they did before.
internal sealed class PostgresUniversalProgressStore(JobStoreDatabase database) : IUniversalProgressStore, IExpiredRowSweep
{
    private const string LiveOperation = "operation_id = @operation_id AND expires_at > now()";
    private const string SnapshotAssignments =
        "operation_type = @operation_type, status = @status, payload = @payload, expires_at = now() + @lifetime";
    private static readonly TimeSpan _defaultLifetime = TimeSpan.FromHours(24);

    private readonly string _progress = database.Table("operation_progress");

    public Task SetProgressAsync(
        string operationId,
        IOperationProgress progress,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            $"""
            INSERT INTO {_progress} (operation_id, operation_type, status, payload, expires_at)
            VALUES (@operation_id, @operation_type, @status, @payload, now() + @lifetime)
            ON CONFLICT (operation_id) DO UPDATE SET {SnapshotAssignments}
            """,
            new SnapshotWrite(operationId, progress, ttl ?? _defaultLifetime),
            guard: null,
            cancellationToken);

    public async Task<ProgressCompareAndSetResult> TrySetProgressAsync(OperationProgressWrite write, OperationStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write.Progress);

        var updated = await WriteAsync(
            $"UPDATE {_progress} SET {SnapshotAssignments} WHERE {LiveOperation} AND status = @guard",
            new SnapshotWrite(write.OperationId, write.Progress, write.Ttl ?? _defaultLifetime),
            expectedStatus.ToString(),
            cancellationToken).ConfigureAwait(false);
        return updated
            ? ProgressCompareAndSetResult.Updated
            : await DescribeRefusalAsync(write.OperationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(OperationProgressWrite write, IOperationProgress expectedProgress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write.Progress);
        ArgumentNullException.ThrowIfNull(expectedProgress);
        if (write.Progress.Type != expectedProgress.Type)
        {
            throw new ArgumentException("A progress update cannot change the operation type.", nameof(write));
        }

        var storedPayload = await ReadPayloadAsync(write.OperationId, cancellationToken).ConfigureAwait(false);
        var current = storedPayload is null ? null : OperationProgressSerializer.Deserialize(storedPayload);
        if (current is null)
        {
            return ProgressCompareAndSetResult.NotFound;
        }

        if (!OperationProgressSerializer.SameSnapshot(current, expectedProgress))
        {
            return ProgressCompareAndSetResult.StatusMismatch(current);
        }

        var updated = await WriteAsync(
            $"UPDATE {_progress} SET {SnapshotAssignments} WHERE {LiveOperation} AND payload = @guard",
            new SnapshotWrite(write.OperationId, write.Progress, write.Ttl ?? _defaultLifetime),
            storedPayload,
            cancellationToken).ConfigureAwait(false);
        return updated
            ? ProgressCompareAndSetResult.Updated
            : await DescribeRefusalAsync(write.OperationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
        => await GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false) as TProgress;

    public async Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var payload = await ReadPayloadAsync(operationId, cancellationToken).ConfigureAwait(false);
        return payload is null ? null : OperationProgressSerializer.Deserialize(payload);
    }

    public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        => database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand($"DELETE FROM {_progress} WHERE operation_id = @operation_id", connection);
                command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operationId });
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public async Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(
        OperationType? operationType = null,
        CancellationToken cancellationToken = default)
        => await ReadColumnAsync("operation_id", operationType, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(
        OperationType operationType,
        CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
    {
        var payloads = await ReadColumnAsync("payload", operationType, cancellationToken).ConfigureAwait(false);
        return payloads.Select(OperationProgressSerializer.Deserialize).OfType<TProgress>().ToList();
    }

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT operation_id FROM {_progress}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_progress} AS snapshot USING expired WHERE snapshot.operation_id = expired.operation_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private Task<bool> WriteAsync(string statement, SnapshotWrite write, string? guard, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                var parameters = command.Parameters;
                parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = write.OperationId });
                parameters.Add(new NpgsqlParameter("operation_type", NpgsqlDbType.Text) { Value = write.Progress.Type.ToString() });
                parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = write.Progress.Status.ToString() });
                parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Text) { Value = OperationProgressSerializer.Serialize(write.Progress) });
                parameters.Add(new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = write.Lifetime });
                if (guard is not null)
                {
                    parameters.Add(new NpgsqlParameter("guard", NpgsqlDbType.Text) { Value = guard });
                }

                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            guard is null ? StatementRepeat.SafeToRepeat : StatementRepeat.NotAfterSend,
            cancellationToken);

    private async Task<ProgressCompareAndSetResult> DescribeRefusalAsync(string operationId, CancellationToken cancellationToken)
    {
        var current = await GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false);
        return current is null ? ProgressCompareAndSetResult.NotFound : ProgressCompareAndSetResult.StatusMismatch(current);
    }

    private Task<string?> ReadPayloadAsync(string operationId, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand($"SELECT payload FROM {_progress} WHERE {LiveOperation}", connection);
                command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operationId });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private Task<List<string>> ReadColumnAsync(string column, OperationType? operationType, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT {column} FROM {_progress}
                    WHERE expires_at > now() AND (@operation_type::text IS NULL OR operation_type = @operation_type)
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("operation_type", NpgsqlDbType.Text)
                {
                    Value = operationType.HasValue ? operationType.Value.ToString() : DBNull.Value
                });
                var values = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    values.Add(reader.GetString(0));
                }

                return values;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private readonly record struct SnapshotWrite(string OperationId, IOperationProgress Progress, TimeSpan Lifetime);
}
