// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed class TileCacheWarmingOptions
{
    public const string SectionName = "TileCacheWarming";

    public bool Enabled { get; set; }

    public List<TileCacheWarmingTarget> Targets { get; set; } = [];
}

internal sealed class TileCacheWarmingTarget
{
    public string Operation { get; set; } = "warm";

    public string? ServiceId { get; set; }

    public int? LayerId { get; set; }

    public int? MinZoom { get; set; }

    public int? MaxZoom { get; set; }

    public string? TileMatrixSetId { get; set; }

    public double[]? Bbox { get; set; }

    public int? MaxTiles { get; set; }
}
