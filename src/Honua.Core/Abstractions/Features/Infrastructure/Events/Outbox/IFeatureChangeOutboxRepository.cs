// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

// Atomic insertion of an outbox row stays inside the provider because it needs an ambient ADO.NET connection and transaction.
public interface IFeatureChangeOutboxRepository
{
    // The claim must be one statement that skips rows another node already holds.
    Task<IReadOnlyList<FeatureChangeOutboxEntry>> ClaimPendingAsync(
        string nodeId,
        int batchSize,
        TimeSpan claimTtl,
        CancellationToken cancellationToken);

    // False means this caller does not hold the active claim, so it must leave the row unchanged.
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
