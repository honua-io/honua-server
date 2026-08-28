// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Non-durable operation-instance store for explicit development and unit-test composition.
/// Production composition must replace it with a durable implementation.
/// </summary>
public sealed class VolatileOperationInstanceStore : IOperationInstanceStore
{
    private readonly ConcurrentDictionary<string, OperationHandle> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Owner, DateTimeOffset Expires)> _leases = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_instances.TryAdd(envelope.OperationInstanceId, envelope));
    }

    /// <inheritdoc />
    public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_instances.ContainsKey(envelope.OperationInstanceId))
        {
            throw new InvalidOperationException($"Operation instance '{envelope.OperationInstanceId}' was not durably accepted.");
        }

        _instances.AddOrUpdate(
            envelope.OperationInstanceId,
            _ => throw new InvalidOperationException($"Operation instance '{envelope.OperationInstanceId}' was not accepted."),
            (_, current) => envelope with { Version = current.Version + 1 });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TrySetAsync(
        OperationHandle envelope,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_instances.TryGetValue(envelope.OperationInstanceId, out var current))
        {
            if (current.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            if (_instances.TryUpdate(
                    envelope.OperationInstanceId,
                    envelope with { Version = expectedVersion + 1 },
                    current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<OperationHandle?> GetAsync(
        string operationInstanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _instances.TryGetValue(operationInstanceId, out var envelope);
        return Task.FromResult(envelope);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<OperationHandle>>(_instances.Values.Where(IsActive).ToArray());
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireLeaseAsync(
        string leaseId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            if (!_leases.TryGetValue(leaseId, out var current))
            {
                return Task.FromResult(_leases.TryAdd(leaseId, (ownerId, now + duration)));
            }

            if (current.Expires > now)
            {
                return Task.FromResult(false);
            }

            if (_leases.TryUpdate(leaseId, (ownerId, now + duration), current))
            {
                return Task.FromResult(true);
            }
        }
    }

    /// <inheritdoc />
    public Task ReleaseLeaseAsync(
        string leaseId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_leases.TryGetValue(leaseId, out var current) &&
            string.Equals(current.Owner, ownerId, StringComparison.Ordinal))
        {
            _leases.TryRemove(new KeyValuePair<string, (string Owner, DateTimeOffset Expires)>(leaseId, current));
        }

        return Task.CompletedTask;
    }

    private static bool IsActive(OperationHandle envelope)
        => envelope.Status is OperationHandleStatus.Accepted
            or OperationHandleStatus.Queued
            or OperationHandleStatus.Running
            or OperationHandleStatus.RequiresApproval;
}
