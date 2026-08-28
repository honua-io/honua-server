// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Durable source of truth for canonical operation-instance envelopes.
/// </summary>
public interface IOperationInstanceStore
{
    /// <summary>Creates an operation instance before authorization or policy evaluation.</summary>
    Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default);

    /// <summary>Persists the latest projection of an existing operation instance.</summary>
    Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an envelope only when its stored version equals <paramref name="expectedVersion"/>.
    /// A false result is a loud transition refusal; callers must re-read the authority.
    /// </summary>
    Task<bool> TrySetAsync(
        OperationHandle envelope,
        long expectedVersion,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>Reads an operation instance by its canonical invocation identity.</summary>
    Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Lists nonterminal operation instances for leased reconciliation.</summary>
    Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OperationHandle>>([]);

    /// <summary>Attempts to acquire a distributed reconciliation lease.</summary>
    Task<bool> TryAcquireLeaseAsync(
        string leaseId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>Releases a reconciliation lease owned by the caller.</summary>
    Task ReleaseLeaseAsync(
        string leaseId,
        string ownerId,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
