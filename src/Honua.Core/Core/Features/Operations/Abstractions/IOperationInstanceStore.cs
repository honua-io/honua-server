// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationInstanceStore
{
    Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default);

    Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default);

    // A false result is a transition refusal; the caller must re-read the authority.
    Task<bool> TrySetAsync(
        OperationHandle envelope,
        long expectedVersion,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OperationHandle>>([]);

    Task<bool> TryAcquireLeaseAsync(
        string leaseId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task ReleaseLeaseAsync(
        string leaseId,
        string ownerId,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
