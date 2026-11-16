// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public interface IOperationProposalStore : IOperationStore
{
    Task<bool> TryCreateAsync(
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<OperationProposal?> GetAsync(
        string proposalId,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetAsync(
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
        OperationClass? kind = null,
        CancellationToken cancellationToken = default);
}
