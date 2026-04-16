// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Redis-backed durable store for workflow definitions.
/// </summary>
internal sealed class RedisWorkflowDefinitionStore(IConnectionMultiplexer redis) : IWorkflowDefinitionStore
{
    private const string DefinitionKeyPrefix = "orchestration:def:";
    private const string DefinitionIndexKey = "orchestration:def:all";

    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetKey(workflowId)).ConfigureAwait(false);
        return payload.HasValue
            ? JsonSerializer.Deserialize(payload.ToString(), OrchestrationJsonContext.Default.WorkflowDefinition)
            : null;
    }

    public async Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey(definition.WorkflowId);
        var payload = JsonSerializer.Serialize(definition, OrchestrationJsonContext.Default.WorkflowDefinition);

        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.KeyNotExists(key));
        var writeTask = transaction.StringSetAsync(key, payload);
        var indexTask = transaction.SetAddAsync(DefinitionIndexKey, definition.WorkflowId);
        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            return false;
        }

        await writeTask.ConfigureAwait(false);
        await indexTask.ConfigureAwait(false);
        return true;
    }

    public async Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey(definition.WorkflowId);
        var payload = JsonSerializer.Serialize(definition, OrchestrationJsonContext.Default.WorkflowDefinition);

        await _database.StringSetAsync(key, payload).ConfigureAwait(false);
        await _database.SetAddAsync(DefinitionIndexKey, definition.WorkflowId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = await _database.SetMembersAsync(DefinitionIndexKey).ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return Array.Empty<WorkflowDefinition>();
        }

        var results = new List<WorkflowDefinition>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!id.HasValue)
            {
                continue;
            }

            var definition = await GetAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (definition != null)
            {
                results.Add(definition);
            }
        }

        return results
            .OrderBy(def => def.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(def => def.Trigger is { Kind: WorkflowTriggerKind.Cron, Enabled: true }
                          && !string.IsNullOrWhiteSpace(def.Trigger.CronExpression))
            .ToArray();
    }

    public async Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        var removed = await _database.KeyDeleteAsync(GetKey(workflowId)).ConfigureAwait(false);
        await _database.SetRemoveAsync(DefinitionIndexKey, workflowId).ConfigureAwait(false);
        return removed;
    }

    private static string GetKey(string workflowId) => DefinitionKeyPrefix + workflowId;
}
