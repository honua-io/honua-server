// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

// Collects one result set in pooled storage and copies it once into an array of the
// returned length, instead of growing a builder and copying it again on completion.
internal sealed class PooledFeatureBuffer : IDisposable
{
    private const int InitialCapacity = 64;

    private Feature[] _items = ArrayPool<Feature>.Shared.Rent(InitialCapacity);

    public int Count { get; private set; }

    public void Add(Feature feature)
    {
        if (Count == _items.Length)
        {
            Grow();
        }

        _items[Count++] = feature;
    }

    public ImmutableArray<Feature> ToImmutable(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count);
        return count == 0
            ? []
            : ImmutableCollectionsMarshal.AsImmutableArray(_items.AsSpan(0, count).ToArray());
    }

    public void Dispose()
    {
        if (_items.Length == 0)
        {
            return;
        }

        // Features hold geometry and attribute references; never leave them in the pool.
        ArrayPool<Feature>.Shared.Return(_items, clearArray: true);
        _items = [];
        Count = 0;
    }

    private void Grow()
    {
        var larger = ArrayPool<Feature>.Shared.Rent(_items.Length * 2);
        _items.AsSpan(0, Count).CopyTo(larger);
        ArrayPool<Feature>.Shared.Return(_items, clearArray: true);
        _items = larger;
    }
}
