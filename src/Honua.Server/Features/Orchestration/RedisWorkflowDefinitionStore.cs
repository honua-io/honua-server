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
    private const string ScheduleClaimKeyPrefix = "orchestration:schedule:claim:";
    private const string ScheduleCursorKeyPrefix = "orchestration:schedule:cursor:";
    private const string SchedulePendingCursorKeyPrefix = "orchestration:schedule:pending-cursor:";

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

        // Clear the durable scheduler cursors so a later recreate of the same workflow id
        // does not inherit the deleted definition's fire history and skip the first run
        // for the replacement. Per-occurrence claim keys carry a short TTL and key off
        // the absolute fire time, so stale claims cannot suppress fresh occurrences; no
        // scan-based cleanup is required.
        await _database.KeyDeleteAsync(GetScheduleCursorKey(workflowId)).ConfigureAwait(false);
        await _database.KeyDeleteAsync(GetSchedulePendingCursorKey(workflowId)).ConfigureAwait(false);
        return removed;
    }

    public async Task<bool> TryClaimScheduleFireAsync(
        string workflowId,
        DateTimeOffset fireTime,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetScheduleClaimKey(workflowId, fireTime);
        var ttl = retention > TimeSpan.Zero ? retention : TimeSpan.FromHours(24);

        // SET NX with TTL atomically claims the fire-time slot across replicas. Restarts
        // inherit the existing claim for the remaining TTL window.
        return await _database.StringSetAsync(
            key,
            Environment.MachineName,
            ttl,
            when: When.NotExists).ConfigureAwait(false);
    }

    public async Task ReleaseScheduleClaimAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        // Best-effort delete. If the claim key has already expired the TTL made it a
        // no-op; either way the slot becomes eligible for a fresh claim on the next tick.
        await _database.KeyDeleteAsync(GetScheduleClaimKey(workflowId, fireTime)).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetScheduleCursorAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset? cursor = null;
        var payload = await _database.StringGetAsync(GetScheduleCursorKey(workflowId)).ConfigureAwait(false);
        if (payload.HasValue &&
            DateTimeOffset.TryParse(
                payload.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            cursor = parsed;
        }

        var pendingPayload = await _database.StringGetAsync(GetSchedulePendingCursorKey(workflowId)).ConfigureAwait(false);
        if (pendingPayload.HasValue &&
            DateTimeOffset.TryParse(
                pendingPayload.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var pendingParsed))
        {
            if (cursor is null || pendingParsed > cursor.Value)
            {
                cursor = pendingParsed;
            }
        }

        return cursor;
    }

    public async Task AdvanceScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetScheduleCursorKey(workflowId);
        var candidate = fireTime.ToUniversalTime();
        var encoded = candidate.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        // Move the cursor forward only: competing replicas and late ticks must never rewind
        // the scheduler past a firing that has already been enumerated locally. Looping lets
        // us retry through concurrent updates without an explicit compare-and-swap primitive.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _database.StringGetAsync(key).ConfigureAwait(false);
            if (!current.HasValue)
            {
                var inserted = await _database.StringSetAsync(
                    key,
                    encoded,
                    when: When.NotExists).ConfigureAwait(false);
                if (inserted)
                {
                    break;
                }

                continue;
            }

            if (!DateTimeOffset.TryParse(
                    current.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var existing))
            {
                // Corrupt or legacy value — overwrite unconditionally with the known-good encoding.
                await _database.StringSetAsync(key, encoded).ConfigureAwait(false);
                break;
            }

            if (existing >= candidate)
            {
                break;
            }

            var transaction = _database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, current));
            _ = transaction.StringSetAsync(key, encoded);
            if (await transaction.ExecuteAsync().ConfigureAwait(false))
            {
                break;
            }
        }

        // The durable cursor now protects against replay; the pending cursor marker is no
        // longer needed. Best-effort cleanup — a failure here is harmless because
        // GetScheduleCursorAsync returns max(cursor, pending).
        try
        {
            await _database.KeyDeleteAsync(GetSchedulePendingCursorKey(workflowId)).ConfigureAwait(false);
        }
        catch
        {
            // Stale pending key is benign: it can only point at a fire time the cursor
            // has already moved past.
        }
    }

    public async Task SetPendingScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();

        var encoded = fireTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await _database.StringSetAsync(
            GetSchedulePendingCursorKey(workflowId),
            encoded).ConfigureAwait(false);
    }

    private static string GetKey(string workflowId) => DefinitionKeyPrefix + workflowId;

    private static string GetScheduleClaimKey(string workflowId, DateTimeOffset fireTime)
    {
        // Round to minute precision so a single occurrence produces one claim key, regardless
        // of any sub-minute clock skew between replicas.
        var minuteStamp = fireTime.ToUniversalTime().ToUnixTimeSeconds() / 60L;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{ScheduleClaimKeyPrefix}{workflowId}:{minuteStamp}");
    }

    private static string GetScheduleCursorKey(string workflowId)
        => ScheduleCursorKeyPrefix + workflowId;

    private static string GetSchedulePendingCursorKey(string workflowId)
        => SchedulePendingCursorKeyPrefix + workflowId;
}
