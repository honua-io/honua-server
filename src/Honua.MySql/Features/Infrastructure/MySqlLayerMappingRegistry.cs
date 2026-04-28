// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.MySql.Features.Infrastructure;

/// <summary>
/// Thread-safe registry of MySQL/MariaDB layer mappings, keyed by layer ID.
/// Built once at startup from <see cref="MySqlOptions"/> and shared across all scoped services.
/// </summary>
internal sealed class MySqlLayerMappingRegistry
{
    private readonly FrozenDictionary<int, MySqlLayerMapping> _mappings;

    public MySqlLayerMappingRegistry(IEnumerable<MySqlLayerMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _mappings = mappings.ToFrozenDictionary(m => m.LayerId);
    }

    /// <summary>Gets the mapping for a given layer ID, or null if not configured.</summary>
    public MySqlLayerMapping? GetMapping(int layerId)
        => _mappings.TryGetValue(layerId, out var mapping) ? mapping : null;

    /// <summary>
    /// Gets the mapping for a given layer ID. Throws when the layer is not configured.
    /// </summary>
    public MySqlLayerMapping GetRequiredMapping(int layerId)
        => _mappings.TryGetValue(layerId, out var mapping)
            ? mapping
            : throw new KeyNotFoundException($"No MySQL/MariaDB layer mapping found for layer {layerId}.");

    /// <summary>All configured layer IDs.</summary>
    public IEnumerable<int> LayerIds => _mappings.Keys;

    /// <summary>All configured layer mappings.</summary>
    public IEnumerable<MySqlLayerMapping> Mappings => _mappings.Values;
}
