// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.ControlPlane;

/// <summary>
/// Redis-backed durable store for deploy workflow operations and reconciliation leases.
/// </summary>
internal sealed partial class RedisWorkflowOperationStore(
    IConnectionMultiplexer redis,
    ILogger<RedisWorkflowOperationStore> logger,
    IOptions<MetadataReleaseOperationOptions>? metadataReleaseOptions = null) : IWorkflowOperationStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);
    private const string RefreshMetadataPackageIndexScript = """
        local current = redis.call('GET', KEYS[1])
        if current == ARGV[1] then
            redis.call('SET', KEYS[1], ARGV[1], 'PX', tonumber(ARGV[2]))
            return 1
        end
        return 0
        """;

    private readonly IDatabase _database = redis.GetDatabase();
    private readonly MetadataReleaseOperationOptions _metadataReleaseOptions =
        metadataReleaseOptions?.Value ?? new MetadataReleaseOperationOptions();

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

        var retention = ResolveRetention(operation, ttl);
        var created = await PersistAsync(operation, retention, createOnly: true).ConfigureAwait(false);
        if (!created)
        {
            return false;
        }

        Log.WorkflowOperationCreated(logger, operation.OperationId, operation.Kind.ToString(), operation.Status.ToString());
        LogMetadataReleaseStage(operation);
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

    public async Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var operationId = await _database.StringGetAsync(GetMetadataPackageIndexKey(packageId.Trim())).ConfigureAwait(false);
        if (!operationId.HasValue)
        {
            return null;
        }

        var operation = await GetAsync(operationId.ToString(), cancellationToken).ConfigureAwait(false);
        if (operation == null)
        {
            Log.MetadataPackageIndexStale(logger, packageId, operationId.ToString());
        }

        return operation;
    }

    public async Task SetAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        await PersistAsync(operation, ResolveRetention(operation, ttl), createOnly: false).ConfigureAwait(false);
        Log.WorkflowOperationUpdated(logger, operation.OperationId, operation.Status.ToString());
        LogMetadataReleaseStage(operation);
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
        QueueMetadataPackageIndexUpdate(transaction, operation, retention, createOnly);
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

    private static void QueueMetadataPackageIndexUpdate(
        ITransaction transaction,
        WorkflowOperationRecord operation,
        TimeSpan retention,
        bool createOnly)
    {
        var packageId = operation.MetadataRelease?.PackageId;
        if (operation.Kind != WorkflowOperationKind.MetadataRelease || string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        var indexKey = GetMetadataPackageIndexKey(packageId.Trim());
        if (createOnly)
        {
            transaction.StringSetAsync(indexKey, operation.OperationId, retention);
            return;
        }

        transaction.ScriptEvaluateAsync(
            RefreshMetadataPackageIndexScript,
            [(RedisKey)indexKey],
            [(RedisValue)operation.OperationId, (RedisValue)(long)retention.TotalMilliseconds]);
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

    private TimeSpan ResolveRetention(WorkflowOperationRecord operation, TimeSpan? ttl)
    {
        if (ttl.HasValue)
        {
            return ttl.Value;
        }

        return operation.Kind == WorkflowOperationKind.MetadataRelease
            ? _metadataReleaseOptions.OperationRetention
            : DefaultRetention;
    }

    private void LogMetadataReleaseStage(WorkflowOperationRecord operation)
    {
        if (operation.Kind != WorkflowOperationKind.MetadataRelease || operation.MetadataRelease == null)
        {
            return;
        }

        Log.MetadataReleaseStageObserved(
            logger,
            operation.OperationId,
            operation.MetadataRelease.PackageId,
            operation.MetadataRelease.CurrentStage.ToString(),
            operation.Status.ToString());
    }

    private static string GetOperationKey(string operationId) => $"controlplane:workflow:{operationId}";

    private static string GetMetadataPackageIndexKey(string packageId) => $"controlplane:workflow:metapkg:{packageId}";

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

        [LoggerMessage(9002, LogLevel.Warning, "Metadata release package index for {PackageId} points to missing operation {OperationId}")]
        public static partial void MetadataPackageIndexStale(
            ILogger logger,
            string packageId,
            string operationId);

        [LoggerMessage(9003, LogLevel.Information, "Metadata release operation {OperationId} for package {PackageId} is at stage {Stage} with status {Status}")]
        public static partial void MetadataReleaseStageObserved(
            ILogger logger,
            string operationId,
            string packageId,
            string stage,
            string status);
    }
}
