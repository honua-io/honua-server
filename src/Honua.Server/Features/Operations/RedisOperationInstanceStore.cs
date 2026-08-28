// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Operations;

/// <summary>Redis-backed durable store for canonical operation-instance envelopes.</summary>
internal sealed class RedisOperationInstanceStore(IConnectionMultiplexer redis) : IOperationInstanceStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const string ActiveInstancesKey = "controlplane:operation-instance:active";
    private const string CompareAndSetScript = """
        local current = redis.call('GET', KEYS[1])
        if current == false then
            return 0
        end
        local decoded = cjson.decode(current)
        if tonumber(decoded.version) ~= tonumber(ARGV[2]) then
            return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'PX', tonumber(ARGV[3]))
        return 1
        """;
    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<bool> TryCreateAsync(
        OperationHandle envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(
            envelope with { Version = 0 },
            OperationsJsonContext.Default.OperationHandle);
        var created = await _database.StringSetAsync(
                Key(envelope.OperationInstanceId),
                payload,
                Retention,
                When.NotExists)
            .ConfigureAwait(false);
        if (created)
        {
            await _database.SetAddAsync(ActiveInstancesKey, envelope.OperationInstanceId).ConfigureAwait(false);
        }

        return created;
    }

    public async Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(envelope.OperationInstanceId);
        if (!await _database.KeyExistsAsync(key).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Operation instance '{envelope.OperationInstanceId}' was not durably accepted.");
        }

        var current = await GetAsync(envelope.OperationInstanceId, cancellationToken).ConfigureAwait(false);
        if (current is null ||
            !await TrySetAsync(envelope, current.Version, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Operation instance '{envelope.OperationInstanceId}' could not be updated.");
        }
    }

    public async Task<bool> TrySetAsync(
        OperationHandle envelope,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(
            envelope with { Version = expectedVersion + 1 },
            OperationsJsonContext.Default.OperationHandle);
        var result = await _database.ScriptEvaluateAsync(
                CompareAndSetScript,
                [(RedisKey)Key(envelope.OperationInstanceId)],
                [(RedisValue)payload, (RedisValue)expectedVersion, (RedisValue)(long)Retention.TotalMilliseconds])
            .ConfigureAwait(false);
        var updated = (int)result == 1;
        if (updated)
        {
            await UpdateActiveIndexAsync(envelope).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<OperationHandle?> GetAsync(
        string operationInstanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _database.StringGetAsync(Key(operationInstanceId)).ConfigureAwait(false);
        return payload.HasValue
            ? JsonSerializer.Deserialize(payload.ToString(), OperationsJsonContext.Default.OperationHandle)
            : null;
    }

    public async Task<IReadOnlyList<OperationHandle>> ListActiveAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = await _database.SetMembersAsync(ActiveInstancesKey).ConfigureAwait(false);
        var active = new List<OperationHandle>(ids.Length);
        foreach (var id in ids)
        {
            var envelope = await GetAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (envelope is null || !IsActive(envelope))
            {
                await _database.SetRemoveAsync(ActiveInstancesKey, id).ConfigureAwait(false);
                continue;
            }

            active.Add(envelope);
        }

        return active;
    }

    public Task<bool> TryAcquireLeaseAsync(
        string leaseId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockTakeAsync(LeaseKey(leaseId), ownerId, duration);
    }

    public Task ReleaseLeaseAsync(
        string leaseId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockReleaseAsync(LeaseKey(leaseId), ownerId);
    }

    private async Task UpdateActiveIndexAsync(OperationHandle envelope)
    {
        if (IsActive(envelope))
        {
            await _database.SetAddAsync(ActiveInstancesKey, envelope.OperationInstanceId).ConfigureAwait(false);
        }
        else
        {
            await _database.SetRemoveAsync(ActiveInstancesKey, envelope.OperationInstanceId).ConfigureAwait(false);
        }
    }

    private static bool IsActive(OperationHandle envelope)
        => envelope.Status is OperationHandleStatus.Accepted
            or OperationHandleStatus.Queued
            or OperationHandleStatus.Running
            or OperationHandleStatus.RequiresApproval;

    private static string Key(string operationInstanceId) => $"controlplane:operation-instance:{operationInstanceId}";

    private static string LeaseKey(string leaseId) => $"controlplane:operation-instance:lease:{leaseId}";
}
