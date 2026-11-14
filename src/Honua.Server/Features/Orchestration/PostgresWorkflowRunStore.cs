// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Orchestration;

internal sealed class PostgresWorkflowRunStore(
    ControlPlaneStorage storage,
    IOptions<ControlPlaneRetentionOptions> retention) : IWorkflowRunStore, IExpiredRowSweep
{
    private readonly string _runs = storage.Database.Table("workflow_runs");
    private readonly OperationLeases _leases = new(storage.Leases, "workflow-run:");
    private const string Columns = "run_id, workflow_id, active, created_at, expires_at, record";
    private const string Values = "@id, @workflow, @active, @created, now() + @retention, @record";

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _leases.TryAcquireAsync(new LeaseClaim(operationId, ownerId), leaseDuration, cancellationToken);

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _leases.RenewAsync(new LeaseClaim(operationId, ownerId), leaseDuration, cancellationToken);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => _leases.ReleaseAsync(new LeaseClaim(operationId, ownerId), cancellationToken);

    public Task<bool> TryCreateAsync(WorkflowRun run, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => WriteAsync($"INSERT INTO {_runs} ({Columns}) VALUES ({Values}) ON CONFLICT (run_id) DO NOTHING",
            new RunWrite(run, ttl ?? retention.Value.WorkflowRuns), cancellationToken);

    public async Task SetAsync(WorkflowRun run, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => await WriteAsync($"""
            INSERT INTO {_runs} ({Columns}) VALUES ({Values})
            ON CONFLICT (run_id) DO UPDATE SET workflow_id = excluded.workflow_id,
                active = excluded.active, created_at = excluded.created_at,
                expires_at = excluded.expires_at, record = excluded.record
            """, new RunWrite(run, ttl ?? retention.Value.WorkflowRuns), cancellationToken).ConfigureAwait(false);

    public Task<WorkflowRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT record::text FROM {_runs} WHERE run_id = @id AND (active OR expires_at > now())", connection);
            command.Parameters.AddWithValue("id", runId);
            var payload = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            return payload is null ? null : Decode(payload);
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListActiveAsync(CancellationToken cancellationToken = default)
        => ReadAsync($"SELECT record::text FROM {_runs} WHERE active ORDER BY created_at, run_id", null, cancellationToken);

    public Task<IReadOnlyList<WorkflowRun>> ListByWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return ReadAsync($"""
            SELECT record::text FROM {_runs} WHERE workflow_id = @workflow
                AND (active OR expires_at > now()) ORDER BY created_at DESC, run_id
            """, workflowId, cancellationToken);
    }

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                DELETE FROM {_runs} WHERE run_id IN (
                    SELECT run_id FROM {_runs} WHERE NOT active AND expires_at <= now()
                    ORDER BY expires_at LIMIT @batch FOR UPDATE SKIP LOCKED)
                """, connection);
            command.Parameters.AddWithValue("batch", batchSize);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    private Task<bool> WriteAsync(string sql, RunWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write.Run);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Run.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Run.WorkflowId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(write.Retention, TimeSpan.Zero);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", write.Run.RunId);
            command.Parameters.AddWithValue("workflow", write.Run.WorkflowId);
            command.Parameters.AddWithValue("active", !IsReconcileComplete(write.Run));
            command.Parameters.AddWithValue("created", write.Run.CreatedAt.ToUniversalTime());
            command.Parameters.AddWithValue("retention", write.Retention);
            command.Parameters.AddWithValue("record", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(write.Run, OrchestrationJsonContext.Default.WorkflowRun));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    private Task<IReadOnlyList<WorkflowRun>> ReadAsync(string sql, string? workflowId, CancellationToken cancellationToken)
        => storage.Database.ExecuteAsync<IReadOnlyList<WorkflowRun>>(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            if (workflowId is not null)
            {
                command.Parameters.AddWithValue("workflow", workflowId);
            }

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var runs = new List<WorkflowRun>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                runs.Add(Decode(reader.GetString(0)));
            }

            return runs;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    private static WorkflowRun Decode(string payload)
        => JsonSerializer.Deserialize(payload, OrchestrationJsonContext.Default.WorkflowRun)
            ?? throw new JsonException("Invalid workflow run record.");

    private static bool IsReconcileComplete(WorkflowRun run)
        => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled
            && run.StepStates.All(step => step.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Failed
                or WorkflowStepStatus.Cancelled or WorkflowStepStatus.Skipped);

    private sealed record RunWrite(WorkflowRun Run, TimeSpan Retention);
}
