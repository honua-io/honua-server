// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// PERFORMANCE OPTIMIZATION: Pooled object policy for dictionary allocations
/// Reduces garbage collection pressure for frequently allocated dictionaries in ReadFeatureAsync
/// </summary>
internal sealed class DictionaryPooledObjectPolicy : PooledObjectPolicy<Dictionary<string, object?>>
{
    public override Dictionary<string, object?> Create() => new();

    public override bool Return(Dictionary<string, object?> obj)
    {
        // Clear the dictionary and return it to the pool if size is reasonable
        // Prevents memory bloat by rejecting oversized dictionaries
        if (obj.Count <= 100)
        {
            obj.Clear();
            return true;
        }
        return false; // Let it be garbage collected if too large
    }
}

/// <summary>
/// PERFORMANCE OPTIMIZATION: Pooled object policy for StringBuilder allocations
/// Reduces garbage collection pressure for frequently allocated StringBuilders in SQL generation
/// </summary>
internal sealed class StringBuilderPooledObjectPolicy : PooledObjectPolicy<StringBuilder>
{
    public override StringBuilder Create() => new();

    public override bool Return(StringBuilder obj)
    {
        // Clear the StringBuilder and return it to the pool if capacity is reasonable
        // Prevents memory bloat by rejecting oversized StringBuilders
        if (obj.Capacity <= 8192) // 8KB capacity limit
        {
            obj.Clear();
            return true;
        }
        return false; // Let it be garbage collected if too large
    }
}
