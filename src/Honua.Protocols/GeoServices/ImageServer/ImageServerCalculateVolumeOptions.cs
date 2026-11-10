// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.GeoServices.ImageServer;

public sealed class ImageServerCalculateVolumeOptions
{
    public const string SectionName = "GeoServices:ImageServer:CalculateVolume";

    [Range(1, 100_000_000)]
    public int MaxPixelsPerGeometry { get; set; } = 4_000_000;

    [Range(1, 1024)]
    public int MaxGeometries { get; set; } = 32;
}
