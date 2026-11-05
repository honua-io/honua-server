// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Infrastructure.MultiTenancy;

internal sealed class InMemoryTenantUsageMeter : ITenantUsageMeter
{
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public long RecordRequest(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return 0;
        }

        // AddOrUpdate keeps the counter consistent under concurrent requests for the same tenant.
        return _counts.AddOrUpdate(tenantId, 1, static (_, current) => current + 1);
    }

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
