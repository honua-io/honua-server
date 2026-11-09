// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

public sealed class TileOptions
{
    public const string SectionName = "TileOptions";

    public int SimplifyZoom { get; set; } = 10;

    public int CacheMaxAge { get; set; } = 3600;

    public Dictionary<string, TilesetCacheLifecycle>? TilesetLifecycle { get; set; }

    public int TileExtent { get; set; } = 4096;

    public int TileBuffer { get; set; } = 256;

    public int MetatileFactor { get; set; } = 1;

    public TileCacheEvictionOptions Eviction { get; set; } = new();
}

public sealed class TileCacheEvictionOptions
{
    public bool Enabled { get; set; }

    public long? MaxEntries { get; set; }

    public long? MaxBytes { get; set; }
}

public sealed class TilesetCacheLifecycle
{
    public int? TtlSeconds { get; set; }
}
