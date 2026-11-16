// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

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
