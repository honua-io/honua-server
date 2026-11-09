// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Exceptions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane;

public sealed class UnavailableOperationProposalStore : IOperationProposalStore
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="store"/> is a real durable
    /// proposal store (not null and not the fail-closed placeholder).
    /// </summary>
    /// <param name="store">Resolved proposal store, possibly null.</param>
    /// <returns>Whether proposals can be persisted.</returns>
    public static bool IsDurable([NotNullWhen(true)] IOperationProposalStore? store)
        => store is not null and not UnavailableOperationProposalStore;

    private static CapabilityUnavailableException Unavailable()
        => new(
            CapabilityUnavailableCodes.DurableControlPlaneDetail,
            CapabilityUnavailableCodes.OperationStoreDependency,
            CapabilityUnavailableCodes.OperationStoreRemediation,
            CapabilityUnavailableCodes.OperationStoreRemediationRef,
            CapabilityUnavailableCodes.ControlPlaneProposalsCapability);

    /// <inheritdoc />
    public Task<bool> TryCreateAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());

    /// <inheritdoc />
    public Task<OperationProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
        => Task.FromException<OperationProposal?>(Unavailable());

    /// <inheritdoc />
    public Task<bool> TrySetAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(OperationClass? kind = null, CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<OperationProposal>>(Unavailable());

    /// <inheritdoc />
    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());

    /// <inheritdoc />
    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());

    /// <inheritdoc />
    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => Task.FromException(Unavailable());
}
