// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

internal sealed record VectorTileStyleOptions
{
    public required string ServiceName { get; init; }
    public required string SourceId { get; init; }
    public required string TileUrl { get; init; }
    public string? StoredMapLibreJson { get; init; }
    public MetadataV2GeometryType GeometryType { get; init; }
    public string? SpriteUrl { get; init; }
    public string? GlyphsUrl { get; init; }
    public string? ServiceUrl { get; init; }
    public TileLimits TileLimits { get; init; } = new();
}
