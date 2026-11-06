// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

// The rollup store is fail-open and is never on a serving path; an outage degrades to the local snapshot.
public interface IOpsHealthRollupStore
{
    Task WriteSampleAsync(OpsHealthRollupSample sample, CancellationToken cancellationToken = default);

    Task<int> DownsampleAndPruneAsync(DateTimeOffset now, OpsHealthRollupRetentionPolicy policy, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpsHealthLatencyRow>> ReadLatencyAsync(OpsHealthRollupTier tier, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpsHealthVitalsRow>> ReadVitalsAsync(OpsHealthRollupTier tier, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpsHealthLatencyRow>> ReadRecentLatencyAsync(DateTimeOffset since, string? excludeReplicaId, CancellationToken cancellationToken = default);
}
