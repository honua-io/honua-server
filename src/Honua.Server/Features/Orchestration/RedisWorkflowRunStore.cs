// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Redis-backed durable store for workflow runs.
/// </summary>
internal sealed class RedisWorkflowRunStore(IConnectionMultiplexer redis) : IWorkflowRunStore
{
    private const string RunKeyPrefix = "orchestration:run:";
    private const string LeaseKeyPrefix = "orchestration:run:lease:";
    private const string ActiveIndexKey = "orchestration:run:active";
    private const string WorkflowIndexPrefix = "orchestration:run:wf:";
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    private readonly IDatabase _database = redis.GetDatabase();

    public Task<bool> TryAcquireLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockTakeAsync(GetLeaseKey(operationId), ownerId, leaseDuration);
    }

    public Task<bool> RenewLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockExtendAsync(GetLeaseKey(operationId), ownerId, leaseDuration);
    }

    public Task ReleaseLeaseAsync(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockReleaseAsync(GetLeaseKey(operationId), ownerId);
    }

    public async Task<bool> TryCreateAsync(
        WorkflowRun run,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();

        return await PersistAsync(run, ttl ?? DefaultRetention, createOnly: true).ConfigureAwait(false);
    }

    public async Task<WorkflowRun?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetRunKey(runId)).ConfigureAwait(false);
        return payload.HasValue
            ? JsonSerializer.Deserialize(payload.ToString(), OrchestrationJsonContext.Default.WorkflowRun)
            : null;
    }

    public async Task SetAsync(
        WorkflowRun run,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();

        await PersistAsync(run, ttl ?? DefaultRetention, createOnly: false).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = await _database.SetMembersAsync(ActiveIndexKey).ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return Array.Empty<WorkflowRun>();
        }

        var runs = new List<WorkflowRun>(ids.Length);
        var stale = new List<RedisValue>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!id.HasValue)
            {
                continue;
            }

            var run = await GetAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (run == null || IsReconcileComplete(run))
            {
                stale.Add(id);
                continue;
            }

            runs.Add(run);
        }

        if (stale.Count > 0)
        {
            foreach (var id in stale)
            {
                await _database.SetRemoveAsync(ActiveIndexKey, id).ConfigureAwait(false);
            }
        }

        return runs
            .OrderBy(run => run.CreatedAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListByWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        var indexKey = GetWorkflowIndexKey(workflowId);
        var ids = await _database.SetMembersAsync(indexKey).ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return Array.Empty<WorkflowRun>();
        }

        var runs = new List<WorkflowRun>(ids.Length);
        var stale = new List<RedisValue>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!id.HasValue)
            {
                continue;
            }

            var run = await GetAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (run == null)
            {
                stale.Add(id);
                continue;
            }

            runs.Add(run);
        }

        if (stale.Count > 0)
        {
            foreach (var id in stale)
            {
                await _database.SetRemoveAsync(indexKey, id).ConfigureAwait(false);
            }
        }

        return runs
            .OrderByDescending(run => run.CreatedAt)
            .ToArray();
    }

    private async Task<bool> PersistAsync(WorkflowRun run, TimeSpan retention, bool createOnly)
    {
        var key = GetRunKey(run.RunId);
        var payload = JsonSerializer.Serialize(run, OrchestrationJsonContext.Default.WorkflowRun);

        var transaction = _database.CreateTransaction();
        if (createOnly)
        {
            transaction.AddCondition(Condition.KeyNotExists(key));
        }

        var writeTask = transaction.StringSetAsync(key, payload, retention);
        var indexMemberId = (RedisValue)run.RunId;
        // Keep the run in the active reconcile set until every step is terminal — a run
        // that is marked Cancelled at the top level can still own queued or running child
        // jobs that the reconcile loop must cascade-cancel before the run is truly done.
        var activeIndexTask = IsReconcileComplete(run)
            ? transaction.SetRemoveAsync(ActiveIndexKey, indexMemberId)
            : transaction.SetAddAsync(ActiveIndexKey, indexMemberId);

        var workflowIndexTask = transaction.SetAddAsync(GetWorkflowIndexKey(run.WorkflowId), indexMemberId);

        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            return false;
        }

        await writeTask.ConfigureAwait(false);
        await activeIndexTask.ConfigureAwait(false);
        await workflowIndexTask.ConfigureAwait(false);
        return true;
    }

    private static bool IsTerminal(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Succeeded
            or WorkflowRunStatus.Failed
            or WorkflowRunStatus.Cancelled;

    private static bool IsStepTerminal(WorkflowStepStatus status)
        => status is WorkflowStepStatus.Succeeded
            or WorkflowStepStatus.Failed
            or WorkflowStepStatus.Cancelled
            or WorkflowStepStatus.Skipped;

    private static bool IsReconcileComplete(WorkflowRun run)
        => IsTerminal(run.Status) && run.StepStates.All(s => IsStepTerminal(s.Status));

    private static string GetRunKey(string runId) => RunKeyPrefix + runId;

    private static string GetLeaseKey(string runId) => LeaseKeyPrefix + runId;

    private static string GetWorkflowIndexKey(string workflowId) => WorkflowIndexPrefix + workflowId;
}
