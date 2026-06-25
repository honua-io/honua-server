// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Core.Features.MultiTenancy.Abstractions;

/// <summary>
/// Per-tenant usage-metering counter hook (issue #346). Records billable signals
/// (request counts today) so a future metering/billing pipeline has a measurement seam.
/// </summary>
/// <remarks>
/// This first increment is an in-memory, process-local counter intended as the
/// instrumentation seam — NOT a durable, cross-node billing ledger. Aggregation,
/// persistence, storage/bandwidth dimensions, and billing export are deferred (Refs #346).
/// Implementations must be safe for concurrent use across requests.
/// </remarks>
public interface ITenantUsageMeter
{
    /// <summary>
    /// Records a single billable request for the supplied tenant and returns the new running
    /// total for that tenant within the current process lifetime.
    /// </summary>
    /// <param name="tenantId">The tenant the request is attributed to.</param>
    /// <returns>The updated request count for the tenant.</returns>
    long RecordRequest(string tenantId);

    /// <summary>
    /// Returns a point-in-time snapshot of the recorded usage for every tenant seen so far.
    /// </summary>
    /// <returns>An immutable snapshot of tenant usage counters.</returns>
    IReadOnlyList<TenantUsageSnapshot> Snapshot();
}

/// <summary>
/// A point-in-time usage measurement for a single tenant.
/// </summary>
/// <param name="TenantId">The tenant the measurement belongs to.</param>
/// <param name="RequestCount">The number of metered requests recorded for the tenant.</param>
public readonly record struct TenantUsageSnapshot(string TenantId, long RequestCount);
