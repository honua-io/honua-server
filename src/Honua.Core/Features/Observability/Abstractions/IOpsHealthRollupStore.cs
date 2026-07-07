// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

/// <summary>
/// Persisted, bounded rollup store for ops-health sections (#2553). Each replica flushes its local
/// serving-latency percentiles and ops vitals into the finest (1-minute) tier; coarser tiers are derived
/// by downsampling and every tier is pruned to a hard retention cap. The store never sits on a serving
/// path — callers treat it as fail-open (a missing registration or an outage degrades to snapshot-only).
/// </summary>
/// <remarks>
/// Only registered for the PostgreSQL provider (the shared metadata store). Consumers resolve it as an
/// optional dependency so DuckDB/MySQL deployments simply run without persisted history.
/// </remarks>
public interface IOpsHealthRollupStore
{
    /// <summary>
    /// Writes (upserts) a per-replica flush into the 1-minute tier. The sample's capture time is truncated
    /// to the minute; a later flush within the same minute replaces the earlier one (last-write-wins).
    /// </summary>
    /// <param name="sample">The per-replica flush to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the flush is persisted.</returns>
    Task WriteSampleAsync(OpsHealthRollupSample sample, CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes the coarser (5-minute, hourly) tiers from the 1-minute tier and prunes every tier past
    /// its retention cap. Idempotent: coarser buckets are recomputed on each pass until their source rows age
    /// out. Returns the number of pruned rows.
    /// </summary>
    /// <param name="now">The current time used to compute prune cut-offs.</param>
    /// <param name="policy">The per-tier retention caps.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The total number of pruned rows across all tiers/tables.</returns>
    Task<int> DownsampleAndPruneAsync(DateTimeOffset now, OpsHealthRollupRetentionPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads per-replica latency rows for a tier within an inclusive time window, ordered by bucket.
    /// </summary>
    /// <param name="tier">The rollup tier (resolution).</param>
    /// <param name="from">The inclusive lower bucket bound (UTC).</param>
    /// <param name="to">The inclusive upper bucket bound (UTC).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The matching latency rows.</returns>
    Task<IReadOnlyList<OpsHealthLatencyRow>> ReadLatencyAsync(OpsHealthRollupTier tier, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads per-replica vitals rows for a tier within an inclusive time window, ordered by bucket.
    /// </summary>
    /// <param name="tier">The rollup tier (resolution).</param>
    /// <param name="from">The inclusive lower bucket bound (UTC).</param>
    /// <param name="to">The inclusive upper bucket bound (UTC).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The matching vitals rows.</returns>
    Task<IReadOnlyList<OpsHealthVitalsRow>> ReadVitalsAsync(OpsHealthRollupTier tier, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads recent 1-minute latency rows since <paramref name="since"/>, optionally excluding one replica.
    /// Used by the live snapshot to merge other replicas' recent flushes with the responding instance's
    /// live in-process window.
    /// </summary>
    /// <param name="since">The inclusive lower bucket bound (UTC).</param>
    /// <param name="excludeReplicaId">A replica id to exclude (the responding instance), or <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The recent latency rows.</returns>
    Task<IReadOnlyList<OpsHealthLatencyRow>> ReadRecentLatencyAsync(DateTimeOffset since, string? excludeReplicaId, CancellationToken cancellationToken = default);
}
