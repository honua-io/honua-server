// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Fail-closed operation store used when Production is composed without Redis.
/// It keeps DI resolution healthy and turns use of the durable surface into a
/// controlled 503 instead of an unhandled missing-service exception (#60).
/// </summary>
internal sealed class UnavailableOperationInstanceStore : IOperationInstanceStore
{
    private static ServiceUnavailableException Unavailable()
        => new CapabilityUnavailableException(
            CapabilityUnavailableCodes.DurableControlPlaneDetail,
            CapabilityUnavailableCodes.RedisDependency,
            CapabilityUnavailableCodes.RedisRemediation,
            CapabilityUnavailableCodes.RedisRemediationRef);

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
