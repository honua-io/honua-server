// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Process-local, thread-safe <see cref="ITenantUsageMeter"/> backing the usage-metering
/// counter hook (issue #346). Registered as a singleton so counts accumulate across requests.
/// </summary>
/// <remarks>
/// This is the instrumentation seam, not a durable billing ledger: counts reset on process
/// restart and are not aggregated across scaled nodes. Persistence, storage/bandwidth
/// dimensions, and billing export are deferred (Refs #346).
/// </remarks>
internal sealed class InMemoryTenantUsageMeter : ITenantUsageMeter
{
    private readonly ConcurrentDictionary<string, long> _counts = new();

    /// <inheritdoc />
    public long RecordRequest(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return 0;
        }

        // AddOrUpdate keeps the counter consistent under concurrent requests for the same tenant.
        return _counts.AddOrUpdate(tenantId, 1, static (_, current) => current + 1);
    }

    /// <inheritdoc />
    public IReadOnlyList<TenantUsageSnapshot> Snapshot()
    {
        // Snapshot the dictionary into a stable, ordered list for deterministic responses.
        return _counts
            .ToArray()
            .OrderBy(static pair => pair.Key, System.StringComparer.Ordinal)
            .Select(static pair => new TenantUsageSnapshot(pair.Key, pair.Value))
            .ToList();
    }
}
