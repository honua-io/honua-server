// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed class TileCacheExpiryOptions
{
    public const string SectionName = "TileCacheExpiry";

    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 3600;

    public List<TileCacheExpiryTarget> Targets { get; set; } = [];
}

// An expiry target must name a service or a layer.
internal sealed class TileCacheExpiryTarget
{
    public string? ServiceId { get; set; }

    public int? LayerId { get; set; }

    public string? TileMatrixSetId { get; set; }
}
