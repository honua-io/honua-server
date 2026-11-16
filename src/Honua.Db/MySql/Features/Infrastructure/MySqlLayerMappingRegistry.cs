// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Db.MySql.Features.Infrastructure;

internal sealed class MySqlLayerMappingRegistry
{
    private readonly FrozenDictionary<int, MySqlLayerMapping> _mappings;

    public MySqlLayerMappingRegistry(IEnumerable<MySqlLayerMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _mappings = mappings.ToFrozenDictionary(m => m.LayerId);
    }

    public MySqlLayerMapping? GetMapping(int layerId)
        => _mappings.TryGetValue(layerId, out var mapping) ? mapping : null;

    public MySqlLayerMapping GetRequiredMapping(int layerId)
        => _mappings.TryGetValue(layerId, out var mapping)
            ? mapping
            : throw new KeyNotFoundException($"No MySQL/MariaDB layer mapping found for layer {layerId}.");

    public IEnumerable<int> LayerIds => _mappings.Keys;

    public IEnumerable<MySqlLayerMapping> Mappings => _mappings.Values;
}
