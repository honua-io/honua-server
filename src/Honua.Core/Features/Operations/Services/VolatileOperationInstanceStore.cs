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

        _instances[envelope.OperationInstanceId] = envelope;
        return Task.CompletedTask;
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
}
