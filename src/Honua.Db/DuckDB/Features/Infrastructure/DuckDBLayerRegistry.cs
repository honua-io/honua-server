// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal sealed class DuckDBLayerRegistry
{
    private readonly FrozenDictionary<int, DuckDBLayerMapping> _layers;

    public DuckDBLayerRegistry(IEnumerable<DuckDBLayerMapping> mappings)
    {
        _layers = mappings.ToFrozenDictionary(m => m.LayerId);
    }

    public DuckDBLayerMapping? GetMapping(int layerId)
        => _layers.TryGetValue(layerId, out var mapping) ? mapping : null;

    public DuckDBLayerMapping GetRequiredMapping(int layerId)
        => _layers.TryGetValue(layerId, out var mapping)
            ? mapping
            : throw new KeyNotFoundException($"No DuckDB layer mapping found for layer {layerId}.");

    public IEnumerable<int> LayerIds => _layers.Keys;

    public IEnumerable<DuckDBLayerMapping> Mappings => _layers.Values;
}
