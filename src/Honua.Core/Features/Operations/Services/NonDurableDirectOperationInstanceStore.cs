// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Invocation-scoped operation-instance store composed over the fail-closed durable store on a
/// host started without Redis (owner decision 20, 2026-10-10).
/// </summary>
/// <remarks>
/// <para>
/// A Redis-off cell is a serving + configure + style/render topology: publishing a service is
/// configuration and applying a style preset is styling. A direct, policy-allowed execution of one of the
/// <see cref="DirectOperationIds"/> involves no governed proposal, so it must not be refused just
/// because the durable instance store is absent. Accepting such an operation records its envelope
/// in this store's private, process-local map instead: the operation still receives an
/// <c>operationInstanceId</c>, <c>correlationId</c>, policy outcome and audit receipts, but the
/// instance record lives only for the lifetime of this store (one dispatcher scope) and is marked
/// with <see cref="NonDurableEvidenceRef"/>.
/// </para>
/// <para>
/// Every other operation, and every operation-instance read the store did not itself accept,
/// still reaches the wrapped durable store and therefore keeps its typed capability-unavailable
/// refusal. A policy decision that needs a proposal (RequireApproval) is refused later by the
/// approval bridge, so the governed path is unchanged.
/// </para>
/// </remarks>
public sealed class NonDurableDirectOperationInstanceStore : IOperationInstanceStore
{
    /// <summary>
    /// Evidence reference stamped on a handle whose instance record was kept only in process
    /// memory. The handle's identities are real and audited, but the instance cannot be read back
    /// through the operation status surfaces.
    /// </summary>
    public const string NonDurableEvidenceRef = "operation-instance:non-durable";

    private readonly IOperationInstanceStore _durable;
    private readonly VolatileOperationInstanceStore _local = new();
    private readonly ConcurrentDictionary<string, byte> _owned = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of <see cref="NonDurableDirectOperationInstanceStore"/>.</summary>
    /// <param name="durable">The fail-closed durable store every other operation keeps using.</param>
    /// <param name="directOperationIds">Operations that may execute directly without a durable record.</param>
    public NonDurableDirectOperationInstanceStore(
        IOperationInstanceStore durable,
        IEnumerable<string> directOperationIds)
    {
        ArgumentNullException.ThrowIfNull(durable);
        ArgumentNullException.ThrowIfNull(directOperationIds);
        _durable = durable;
        DirectOperationIds = directOperationIds.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Operations whose direct execution is accepted without the durable store.</summary>
    public IReadOnlySet<string> DirectOperationIds { get; }

    /// <summary>
    /// Whether <paramref name="operationInstanceId"/> was accepted by this store rather than the
    /// durable store.
    /// </summary>
    /// <param name="operationInstanceId">Canonical operation instance id.</param>
    /// <returns><see langword="true"/> when the instance record is not durable.</returns>
    public bool IsNonDurable(string? operationInstanceId)
        => operationInstanceId is not null && _owned.ContainsKey(operationInstanceId);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return IsNonDurable(envelope.OperationInstanceId)
            ? _local.SetAsync(envelope, cancellationToken)
            : _durable.SetAsync(envelope, cancellationToken);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default)
        => IsNonDurable(operationInstanceId)
            ? _local.GetAsync(operationInstanceId, cancellationToken)
            : _durable.GetAsync(operationInstanceId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
        => _durable.ListActiveAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryAcquireLeaseAsync(
        string leaseId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
        => _durable.TryAcquireLeaseAsync(leaseId, ownerId, duration, cancellationToken);

    /// <inheritdoc />
    public Task ReleaseLeaseAsync(string leaseId, string ownerId, CancellationToken cancellationToken = default)
        => _durable.ReleaseLeaseAsync(leaseId, ownerId, cancellationToken);
}
