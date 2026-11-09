// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

// Without Redis, a direct policy-allowed configure or style operation is recorded in process memory; every other operation still receives the durable store's refusal.
public sealed class NonDurableDirectOperationInstanceStore : IOperationInstanceStore
{
    // The handle's identities are real, but the instance cannot be read back through the operation status surfaces.
    public const string NonDurableEvidenceRef = "operation-instance:non-durable";

    private readonly IOperationInstanceStore _durable;
    private readonly VolatileOperationInstanceStore _local = new();
    private readonly ConcurrentDictionary<string, byte> _owned = new(StringComparer.Ordinal);

    public NonDurableDirectOperationInstanceStore(
        IOperationInstanceStore durable,
        IEnumerable<string> directOperationIds)
    {
        ArgumentNullException.ThrowIfNull(durable);
        ArgumentNullException.ThrowIfNull(directOperationIds);
        _durable = durable;
        DirectOperationIds = directOperationIds.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> DirectOperationIds { get; }

    public bool IsNonDurable(string? operationInstanceId)
        => operationInstanceId is not null && _owned.ContainsKey(operationInstanceId);

    public async Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!DirectOperationIds.Contains(envelope.OperationId))
        {
            return await _durable.TryCreateAsync(envelope, cancellationToken).ConfigureAwait(false);
        }

        if (!await _local.TryCreateAsync(envelope, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _owned.TryAdd(envelope.OperationInstanceId, 0);
        return true;
    }

    public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return IsNonDurable(envelope.OperationInstanceId)
            ? _local.SetAsync(envelope, cancellationToken)
            : _durable.SetAsync(envelope, cancellationToken);
    }

    public Task<bool> TrySetAsync(
        OperationHandle envelope,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return IsNonDurable(envelope.OperationInstanceId)
            ? _local.TrySetAsync(envelope, expectedVersion, cancellationToken)
            : _durable.TrySetAsync(envelope, expectedVersion, cancellationToken);
    }

    public Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default)
        => IsNonDurable(operationInstanceId)
            ? _local.GetAsync(operationInstanceId, cancellationToken)
            : _durable.GetAsync(operationInstanceId, cancellationToken);

    public Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
        => _durable.ListActiveAsync(cancellationToken);

    public Task<bool> TryAcquireLeaseAsync(
        string leaseId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
        => _durable.TryAcquireLeaseAsync(leaseId, ownerId, duration, cancellationToken);

    public Task ReleaseLeaseAsync(string leaseId, string ownerId, CancellationToken cancellationToken = default)
        => _durable.ReleaseLeaseAsync(leaseId, ownerId, cancellationToken);
}
