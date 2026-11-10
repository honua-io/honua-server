// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

public static class TilesetTtlResolver
{
    public static string BuildKey(string serviceId, string layerId, string tileMatrixSetId)
        => $"{serviceId}/{layerId}/{tileMatrixSetId}";

    public static int Resolve(
        TileOptions options,
        string serviceId,
        string layerId,
        string tileMatrixSetId)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = BuildKey(serviceId, layerId, tileMatrixSetId);
        return Resolve(options, key);
    }

    public static int Resolve(TileOptions options, string tilesetKey)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrEmpty(tilesetKey)
            && options.TilesetLifecycle is { Count: > 0 } lifecycle
            && lifecycle.TryGetValue(tilesetKey, out var entry)
            && entry?.TtlSeconds is { } ttl)
        {
            return ttl;
        }

        return options.CacheMaxAge;
    }
}
