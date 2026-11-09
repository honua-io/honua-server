// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal sealed class UnavailableOperationInstanceStore : IOperationInstanceStore
{
    private static CapabilityUnavailableException Unavailable()
        => new CapabilityUnavailableException(
            CapabilityUnavailableCodes.DurableControlPlaneDetail,
            CapabilityUnavailableCodes.OperationStoreDependency,
            CapabilityUnavailableCodes.OperationStoreRemediation,
            CapabilityUnavailableCodes.OperationStoreRemediationRef);

    public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());

    public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        => Task.FromException(Unavailable());

    public Task<bool> TrySetAsync(OperationHandle envelope, long expectedVersion, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());

    public Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default)
        => Task.FromException<OperationHandle?>(Unavailable());

    public Task<IReadOnlyList<OperationHandle>> ListActiveAsync(CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<OperationHandle>>(Unavailable());

    public Task<bool> TryAcquireLeaseAsync(string leaseId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());

    public Task ReleaseLeaseAsync(string leaseId, string ownerId, CancellationToken cancellationToken = default)
        => Task.FromException(Unavailable());
}
