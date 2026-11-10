// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed partial class PostgresWorkflowOperationStore
{
    public async Task<WorkflowOperationRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default)
        => await ReadFirstAsync(
            $"SELECT record FROM {_operations} WHERE operation_id = @value",
            operationId,
            cancellationToken).ConfigureAwait(false);

    public async Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(packageId)
            ? null
            : await ReadFirstAsync(
                $"""
                SELECT record FROM {_operations}
                WHERE metadata_package_id = @value
                ORDER BY insert_sequence DESC LIMIT 1
                """,
                packageId.Trim(),
                cancellationToken).ConfigureAwait(false);

    public async Task<WorkflowOperationRecord?> GetMostRecentSucceededDeployByTargetAsync(
        string targetId,
        CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(targetId)
            ? null
            : await ReadFirstAsync(
                $"""
                SELECT record FROM {_operations}
                WHERE kind = 'Deploy' AND deploy_target_id = @value AND status = 'Succeeded'
                ORDER BY COALESCE(completed_at, updated_at) DESC LIMIT 1
                """,
                targetId.Trim(),
                cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(
        WorkflowOperationKind? kind = null,
        CancellationToken cancellationToken = default)
        => await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT record FROM {_operations}
                    WHERE active AND (@kind::text IS NULL OR kind = @kind)
                    ORDER BY updated_at DESC
                    """,
                    connection);
                command.Parameters.Add(OptionalText("kind", kind?.ToString()));
                return await ReadAllAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<WorkflowOperationPage> QueryAsync(
        WorkflowOperationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? DefaultPageSize : query.PageSize, 1, MaxPageSize);
        var skip = (page - 1) * pageSize;
        const string Filter = "(@kind::text IS NULL OR kind = @kind) AND (@status::text IS NULL OR status = @status)";

        var (totalCount, items) = await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var batch = new NpgsqlBatch(connection);
                batch.BatchCommands.Add(FilteredCommand($"SELECT count(*) FROM {_operations} WHERE {Filter}", query));
                var pageCommand = FilteredCommand(
                    $"""
                    SELECT record FROM {_operations} WHERE {Filter}
                    ORDER BY created_at DESC, operation_id DESC LIMIT @page_size OFFSET @skip
                    """,
                    query);
                pageCommand.Parameters.Add(new NpgsqlParameter("page_size", NpgsqlDbType.Integer) { Value = pageSize });
                pageCommand.Parameters.Add(new NpgsqlParameter("skip", NpgsqlDbType.Integer) { Value = skip });
                batch.BatchCommands.Add(pageCommand);

                await using var reader = await batch.ExecuteReaderAsync(token).ConfigureAwait(false);
                await reader.ReadAsync(token).ConfigureAwait(false);
                var count = reader.GetInt64(0);
                await reader.NextResultAsync(token).ConfigureAwait(false);
                var records = new List<WorkflowOperationRecord>(pageSize);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    records.Add(Deserialize(reader.GetString(0)));
                }

                return (count, records);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

        return new WorkflowOperationPage
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = (int)Math.Min(totalCount, int.MaxValue),
            HasMore = skip + items.Count < totalCount,
            IsTruncated = false
        };
    }

    public async Task<bool?> HasLaterDeployOfTargetAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Kind != WorkflowOperationKind.Deploy || string.IsNullOrWhiteSpace(operation.Deploy?.TargetId))
        {
            return null;
        }

        return await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT EXISTS (
                        SELECT 1 FROM {_operations} AS later
                        WHERE later.kind = 'Deploy' AND later.deploy_target_id = known.deploy_target_id
                          AND later.created_at > known.created_at)
                    FROM {_operations} AS known
                    WHERE known.operation_id = @operation_id AND known.kind = 'Deploy'
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operation.OperationId });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as bool?;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<WorkflowOperationRecord?> ReadFirstAsync(string statement, string value, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = value });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string record
                    ? Deserialize(record)
                    : null;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static async Task<List<WorkflowOperationRecord>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var records = new List<WorkflowOperationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Deserialize(reader.GetString(0)));
        }

        return records;
    }

    private static NpgsqlBatchCommand FilteredCommand(string statement, WorkflowOperationQuery query)
    {
        var command = new NpgsqlBatchCommand(statement);
        command.Parameters.Add(OptionalText("kind", query.Kind?.ToString()));
        command.Parameters.Add(OptionalText("status", query.Status?.ToString()));
        return command;
    }

    private static NpgsqlParameter OptionalText(string name, string? value)
        => new(name, NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value };
}
