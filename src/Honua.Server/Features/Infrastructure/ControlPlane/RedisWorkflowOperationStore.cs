// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Redis-backed durable store for deploy workflow operations and reconciliation leases.
/// </summary>
internal sealed partial class RedisWorkflowOperationStore(
    IConnectionMultiplexer redis,
    ILogger<RedisWorkflowOperationStore> logger) : IWorkflowOperationStore
{
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
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var created = await PersistAsync(operation, ttl ?? DefaultRetention, createOnly: true).ConfigureAwait(false);
        if (!created)
        {
            return false;
        }

        Log.WorkflowOperationCreated(logger, operation.OperationId, operation.Kind.ToString(), operation.Status.ToString());
        return true;
    }

    public async Task<WorkflowOperationRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetOperationKey(operationId)).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize(payload.ToString(), ControlPlaneJsonContext.Default.WorkflowOperationRecord);
    }

    public async Task SetAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        await PersistAsync(operation, ttl ?? DefaultRetention, createOnly: false).ConfigureAwait(false);
        Log.WorkflowOperationUpdated(logger, operation.OperationId, operation.Status.ToString());
    }

    public async Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(
        WorkflowOperationKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeKey = kind.HasValue ? GetKindActiveKey(kind.Value) : ActiveOperationsKey;
        var operationIds = await _database.SetMembersAsync(activeKey).ConfigureAwait(false);
        var operations = new List<WorkflowOperationRecord>(operationIds.Length);
        var staleIds = new List<RedisValue>();

        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!operationId.HasValue)
            {
                continue;
            }

            var operation = await GetAsync(operationId.ToString(), cancellationToken).ConfigureAwait(false);
            if (operation == null || IsTerminal(operation.Status))
            {
                staleIds.Add(operationId);
                continue;
            }

            operations.Add(operation);
        }

        if (staleIds.Count > 0)
        {
            await RemoveStaleMembersAsync(activeKey, staleIds).ConfigureAwait(false);
        }

        return operations
            .OrderByDescending(operation => operation.UpdatedAt)
            .ToArray();
    }

    private async Task<bool> PersistAsync(
        WorkflowOperationRecord operation,
        TimeSpan retention,
        bool createOnly)
    {
        var operationKey = GetOperationKey(operation.OperationId);
        var transaction = _database.CreateTransaction();
        if (createOnly)
        {
            transaction.AddCondition(Condition.KeyNotExists(operationKey));
        }

        var payload = JsonSerializer.Serialize(operation, ControlPlaneJsonContext.Default.WorkflowOperationRecord);
        var writeTask = transaction.StringSetAsync(operationKey, payload, retention);
        QueueActiveIndexUpdates(transaction, operation);

        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            return false;
        }

        await writeTask.ConfigureAwait(false);
        return true;
    }

    private static void QueueActiveIndexUpdates(ITransaction transaction, WorkflowOperationRecord operation)
    {
        var operationId = (RedisValue)operation.OperationId;
        if (IsTerminal(operation.Status))
        {
            transaction.SetRemoveAsync(ActiveOperationsKey, operationId);
            transaction.SetRemoveAsync(GetKindActiveKey(operation.Kind), operationId);
            return;
        }

        transaction.SetAddAsync(ActiveOperationsKey, operationId);
        transaction.SetAddAsync(GetKindActiveKey(operation.Kind), operationId);
    }

    private async Task RemoveStaleMembersAsync(string activeKey, IReadOnlyList<RedisValue> staleIds)
    {
        foreach (var staleId in staleIds)
        {
            await _database.SetRemoveAsync(activeKey, staleId).ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    private static string GetOperationKey(string operationId) => $"controlplane:workflow:{operationId}";

    private static string GetLeaseKey(string operationId) => $"controlplane:workflow:lease:{operationId}";

    private static string GetKindActiveKey(WorkflowOperationKind kind)
        => $"controlplane:workflow:active:{kind.ToString().ToLowerInvariant()}";

    private const string ActiveOperationsKey = "controlplane:workflow:active";

    private static partial class Log
    {
        [LoggerMessage(9000, LogLevel.Information, "Created workflow operation {OperationId} ({Kind}) with status {Status}")]
        public static partial void WorkflowOperationCreated(
            ILogger logger,
            string operationId,
            string kind,
            string status);

        [LoggerMessage(9001, LogLevel.Debug, "Updated workflow operation {OperationId} to status {Status}")]
        public static partial void WorkflowOperationUpdated(
            ILogger logger,
            string operationId,
            string status);
    }
}
