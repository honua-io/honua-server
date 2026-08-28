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
    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<bool> TryCreateAsync(
        OperationHandle envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(envelope, OperationsJsonContext.Default.OperationHandle);
        return await _database.StringSetAsync(
                Key(envelope.OperationInstanceId),
                payload,
                Retention,
                When.NotExists)
            .ConfigureAwait(false);
    }

    public async Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(envelope.OperationInstanceId);
        if (!await _database.KeyExistsAsync(key).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Operation instance '{envelope.OperationInstanceId}' was not durably accepted.");
        }

        var payload = JsonSerializer.Serialize(envelope, OperationsJsonContext.Default.OperationHandle);
        if (!await _database.StringSetAsync(key, payload, Retention, When.Exists).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Operation instance '{envelope.OperationInstanceId}' could not be updated.");
        }
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

    private static string Key(string operationInstanceId) => $"controlplane:operation-instance:{operationInstanceId}";
}
