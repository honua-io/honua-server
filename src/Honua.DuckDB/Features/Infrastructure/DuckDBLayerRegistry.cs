// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Thread-safe registry of DuckDB layer mappings, keyed by layer ID.
/// Built once at startup and shared across all scoped services.
/// </summary>
internal sealed class DuckDBLayerRegistry
{
    private readonly FrozenDictionary<int, DuckDBLayerMapping> _layers;

    public DuckDBLayerRegistry(IEnumerable<DuckDBLayerMapping> mappings)
    {
        _layers = mappings.ToFrozenDictionary(m => m.LayerId);
    }

    /// <summary>
    /// Gets the mapping for a given layer ID, or null if not configured.
    /// </summary>
    public DuckDBLayerMapping? GetMapping(int layerId)
        => _layers.TryGetValue(layerId, out var mapping) ? mapping : null;

    /// <summary>
    /// Gets the mapping for a given layer ID.
    /// Throws if the layer is not configured.
    /// </summary>
    public DuckDBLayerMapping GetRequiredMapping(int layerId)
        => _layers.TryGetValue(layerId, out var mapping)
            ? mapping
            : throw new KeyNotFoundException($"No DuckDB layer mapping found for layer {layerId}.");

    /// <summary>All configured layer IDs.</summary>
    public IEnumerable<int> LayerIds => _layers.Keys;
}
