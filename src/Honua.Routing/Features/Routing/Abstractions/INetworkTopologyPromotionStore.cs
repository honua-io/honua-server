// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

public interface INetworkTopologyPromotionStore
{
    Task<NetworkTopologyPromotionRecord> PromoteAsync(
        string datasetId,
        long candidateGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion,
        string actor,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<NetworkTopologyPromotionRecord> RollbackAsync(
        string datasetId,
        long targetGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion,
        string actor,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkTopologyPromotionRecord>> ListHistoryAsync(
        string datasetId,
        CancellationToken cancellationToken = default);
}
