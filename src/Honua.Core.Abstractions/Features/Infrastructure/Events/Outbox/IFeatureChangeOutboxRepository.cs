// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

public interface IFeatureChangeOutboxRepository
{
    // The claim must be one statement that skips rows another node already holds.
    Task<IReadOnlyList<FeatureChangeOutboxEntry>> ClaimPendingAsync(
        string nodeId,
        int batchSize,
        TimeSpan claimTtl,
        CancellationToken cancellationToken);

    // A false return means another node owns the row, so this caller must not overwrite that state.
    Task<bool> MarkDispatchedAsync(Guid outboxId, string ownerNodeId, CancellationToken cancellationToken);

    Task<bool> MarkFailedAsync(
        Guid outboxId,
        string ownerNodeId,
        string errorMessage,
        int maxRetries,
        CancellationToken cancellationToken);

    Task<int> RecoverExpiredClaimsAsync(CancellationToken cancellationToken);

    Task<OutboxBacklogMetrics> GetBacklogMetricsAsync(CancellationToken cancellationToken);
}
