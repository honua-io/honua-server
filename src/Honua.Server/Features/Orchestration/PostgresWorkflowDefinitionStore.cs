// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Orchestration;

internal sealed partial class PostgresWorkflowDefinitionStore(ControlPlaneStorage storage) : IWorkflowDefinitionStore
{
    private readonly string _definitions = storage.Database.Table("workflow_definitions");
    private readonly string _schedules = storage.Database.Table("workflow_schedule_cursors");
    private readonly string _triggers = storage.Database.Table("workflow_trigger_cursors");

    public Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT record::text FROM {_definitions} WHERE workflow_id = @id", connection);
            command.Parameters.AddWithValue("id", workflowId);
            var payload = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            return payload is null ? null : Decode(payload);
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }

    public Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
        => WriteAsync($"""
            INSERT INTO {_definitions} (workflow_id, record) VALUES (@id, @record)
            ON CONFLICT (workflow_id) DO NOTHING
            """, definition, cancellationToken);

    public async Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
        => await WriteAsync($"""
            INSERT INTO {_definitions} (workflow_id, record) VALUES (@id, @record)
            ON CONFLICT (workflow_id) DO UPDATE SET record = excluded.record
            """, definition, cancellationToken).ConfigureAwait(false);

    public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                WITH schedules AS (DELETE FROM {_schedules} WHERE workflow_id = @id),
                     triggers AS (DELETE FROM {_triggers} WHERE workflow_id = @id)
                DELETE FROM {_definitions} WHERE workflow_id = @id RETURNING workflow_id
                """, connection);
            command.Parameters.AddWithValue("id", workflowId);
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string;
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => storage.Database.ExecuteAsync<IReadOnlyList<WorkflowDefinition>>(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"SELECT record::text FROM {_definitions}", connection);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var definitions = new List<WorkflowDefinition>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                definitions.Add(Decode(reader.GetString(0)));
            }

            return definitions.OrderBy(definition => definition.Name, StringComparer.Ordinal).ToArray();
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public async Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(definition => definition.Trigger is { Kind: WorkflowTriggerKind.Cron, Enabled: true }
                && !string.IsNullOrWhiteSpace(definition.Trigger.CronExpression)).ToArray();

    public async Task<IReadOnlyList<WorkflowDefinition>> ListEventTriggeredAsync(CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(definition => definition.Trigger is { Enabled: true } trigger
                && trigger.Kind is WorkflowTriggerKind.ChangeFeed or WorkflowTriggerKind.ObjectStore).ToArray();

    private Task<bool> WriteAsync(string sql, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.WorkflowId);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", definition.WorkflowId);
            command.Parameters.AddWithValue("record", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(definition, OrchestrationJsonContext.Default.WorkflowDefinition));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    private static WorkflowDefinition Decode(string payload)
        => JsonSerializer.Deserialize(payload, OrchestrationJsonContext.Default.WorkflowDefinition)
            ?? throw new JsonException("Invalid workflow definition record.");
}
