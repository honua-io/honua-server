// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

public readonly record struct OperationProposalBatchCandidate(string BatchKey, string IdempotencyKey, string ProposalId);

public interface IOperationProposalBatchStore
{
    Task<string> ReserveBatchAsync(OperationProposalBatchCandidate candidate, CancellationToken cancellationToken = default);

    Task RetargetOpenBatchAsync(string previousProposalId, string renewedProposalId, CancellationToken cancellationToken = default);

    Task CompleteOpenBatchAsync(string proposalId, CancellationToken cancellationToken = default);
}
