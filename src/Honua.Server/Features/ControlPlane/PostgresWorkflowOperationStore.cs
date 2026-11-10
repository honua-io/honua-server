// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Coordination;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed partial class PostgresWorkflowOperationStore(
    JobStoreDatabase database,
    ILeaseStore leases,
    IOptions<ControlPlaneRetentionOptions> retention,
    IOptions<MetadataReleaseOperationOptions> metadataReleaseOptions,
    ILogger<PostgresWorkflowOperationStore> logger) : IWorkflowOperationStore, IExpiredRowSweep
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const string InsertColumns =
        "operation_id, version, kind, status, active, created_at, updated_at, completed_at, "
        + "deploy_target_id, metadata_package_id, expires_at, record";
    private const string InsertValues =
        "@operation_id, @version, @kind, @status, @active, @created_at, @updated_at, @completed_at, "
        + "@deploy_target_id, @metadata_package_id, now() + @retention, @record";
    private const string RecordAssignments =
        "version = @version, kind = @kind, status = @status, active = @active, created_at = @created_at, "
        + "updated_at = @updated_at, completed_at = @completed_at, deploy_target_id = @deploy_target_id, "
        + "metadata_package_id = @metadata_package_id, expires_at = now() + @retention, record = @record";

    private readonly string _operations = database.Table("workflow_operations");
    private readonly OperationLeases _operationLeases = new(leases, "workflow-operation:");

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _operationLeases.TryAcquireAsync(new LeaseClaim(operationId, ownerId), leaseDuration, cancellationToken);

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _operationLeases.RenewAsync(new LeaseClaim(operationId, ownerId), leaseDuration, cancellationToken);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => _operationLeases.ReleaseAsync(new LeaseClaim(operationId, ownerId), cancellationToken);

    public async Task<bool> TryCreateAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var created = operation with { Version = 1 };
        var inserted = await WriteAsync(
            $"INSERT INTO {_operations} ({InsertColumns}) VALUES ({InsertValues}) ON CONFLICT (operation_id) DO NOTHING",
            new OperationWrite(created, ResolveRetention(operation, ttl), ExpectedVersion: null),
            cancellationToken).ConfigureAwait(false);
        if (inserted)
        {
            Log.WorkflowOperationCreated(logger, created.OperationId, created.Kind.ToString(), created.Status.ToString());
            LogMetadataReleaseStage(created);
        }

        return inserted;
    }

    public async Task SetAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var stored = operation with { Version = operation.Version + 1 };
        await WriteAsync(
            $"""
            INSERT INTO {_operations} ({InsertColumns}) VALUES ({InsertValues})
            ON CONFLICT (operation_id) DO UPDATE SET {RecordAssignments}
            """,
            new OperationWrite(stored, ResolveRetention(operation, ttl), ExpectedVersion: null),
            cancellationToken).ConfigureAwait(false);
        LogUpdated(stored);
    }

    public async Task<bool> TrySetAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var stored = operation with { Version = operation.Version + 1 };
        var updated = await WriteAsync(
            $"UPDATE {_operations} SET {RecordAssignments} WHERE operation_id = @operation_id AND version = @expected_version",
            new OperationWrite(stored, ResolveRetention(operation, ttl), operation.Version),
            cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            Log.VersionConflict(logger, operation.OperationId, operation.Version);
            return false;
        }

        LogUpdated(stored);
        return true;
    }

    // A deploy stays while an older deploy of its target is retained, so the retained deploys of a
    // target always include every deploy later than any one of them.
    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT operation_id FROM {_operations} AS candidate
                        WHERE NOT candidate.active AND candidate.expires_at <= now()
                          AND NOT (candidate.kind = 'Deploy' AND candidate.deploy_target_id IS NOT NULL AND EXISTS (
                              SELECT 1 FROM {_operations} AS older
                              WHERE older.kind = 'Deploy' AND older.deploy_target_id = candidate.deploy_target_id
                                AND older.created_at < candidate.created_at
                                AND (older.active OR older.expires_at > now())))
                        ORDER BY candidate.expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_operations} AS operation USING expired
                    WHERE operation.operation_id = expired.operation_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private Task<bool> WriteAsync(string statement, OperationWrite write, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                AddWriteParameters(command, write);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    private static void AddWriteParameters(NpgsqlCommand command, OperationWrite write)
    {
        var operation = write.Operation;
        var packageId = operation.Kind == WorkflowOperationKind.MetadataRelease
            ? operation.MetadataRelease?.PackageId?.Trim()
            : null;
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operation.OperationId });
        parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = operation.Version });
        parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = operation.Kind.ToString() });
        parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = operation.Status.ToString() });
        parameters.Add(new NpgsqlParameter("active", NpgsqlDbType.Boolean) { Value = !IsTerminal(operation.Status) });
        parameters.Add(new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = operation.CreatedAt.ToUniversalTime() });
        parameters.Add(new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = operation.UpdatedAt.ToUniversalTime() });
        parameters.Add(new NpgsqlParameter("completed_at", NpgsqlDbType.TimestampTz)
        {
            Value = operation.CompletedAt.HasValue ? operation.CompletedAt.Value.ToUniversalTime() : DBNull.Value
        });
        parameters.Add(OptionalText("deploy_target_id", operation.Kind == WorkflowOperationKind.Deploy ? operation.Deploy?.TargetId : null));
        parameters.Add(OptionalText("metadata_package_id", packageId));
        parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = write.Retention });
        parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json)
        {
            Value = JsonSerializer.Serialize(operation, ControlPlaneJsonContext.Default.WorkflowOperationRecord)
        });
        if (write.ExpectedVersion.HasValue)
        {
            parameters.Add(new NpgsqlParameter("expected_version", NpgsqlDbType.Bigint) { Value = write.ExpectedVersion.Value });
        }
    }

    private static WorkflowOperationRecord Deserialize(string record)
        => JsonSerializer.Deserialize(record, ControlPlaneJsonContext.Default.WorkflowOperationRecord)
            ?? throw new InvalidDataException("A stored workflow operation record is empty.");

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    private TimeSpan ResolveRetention(WorkflowOperationRecord operation, TimeSpan? ttl)
        => ttl
            ?? (operation.Kind == WorkflowOperationKind.MetadataRelease
                ? metadataReleaseOptions.Value.OperationRetention
                : retention.Value.WorkflowOperations);

    private void LogUpdated(WorkflowOperationRecord operation)
    {
        Log.WorkflowOperationUpdated(logger, operation.OperationId, operation.Status.ToString());
        LogMetadataReleaseStage(operation);
    }

    private void LogMetadataReleaseStage(WorkflowOperationRecord operation)
    {
        if (operation.Kind == WorkflowOperationKind.MetadataRelease && operation.MetadataRelease is { } release)
        {
            Log.MetadataReleaseStageObserved(
                logger,
                operation.OperationId,
                release.PackageId,
                release.CurrentStage.ToString(),
                operation.Status.ToString());
        }
    }

    private readonly record struct OperationWrite(WorkflowOperationRecord Operation, TimeSpan Retention, long? ExpectedVersion);

    private static partial class Log
    {
        [LoggerMessage(9000, LogLevel.Information, "Created workflow operation {OperationId} ({Kind}) with status {Status}")]
        public static partial void WorkflowOperationCreated(ILogger logger, string operationId, string kind, string status);

        [LoggerMessage(9001, LogLevel.Debug, "Updated workflow operation {OperationId} to status {Status}")]
        public static partial void WorkflowOperationUpdated(ILogger logger, string operationId, string status);

        [LoggerMessage(9003, LogLevel.Information, "Metadata release operation {OperationId} for package {PackageId} is at stage {Stage} with status {Status}")]
        public static partial void MetadataReleaseStageObserved(
            ILogger logger,
            string operationId,
            string packageId,
            string stage,
            string status);

        [LoggerMessage(9004, LogLevel.Debug, "Workflow operation {OperationId} version conflict at version {Version}")]
        public static partial void VersionConflict(ILogger logger, string operationId, long version);
    }
}
