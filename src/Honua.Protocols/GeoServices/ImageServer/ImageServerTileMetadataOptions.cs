// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.GeoServices.ImageServer;

// Tile-cache advertisement stays off by default because the ArcGIS Maps SDK for .NET treats singleFusedMapCache as a cached service and then rejects dynamic exportImage.
public sealed class ImageServerTileMetadataOptions
{
    public const string SectionName = "GeoServices:ImageServer:TileMetadata";

    // WebMercatorQuad tile addressing in the shared tile handler stops at level 28.
    public const int HighestServableLevel = 28;

    public bool Enabled { get; set; }

    [Range(0, HighestServableLevel)]
    public int MaxLevel { get; set; } = 23;

    internal bool HasServableMaxLevel => MaxLevel is >= 0 and <= HighestServableLevel;
}
