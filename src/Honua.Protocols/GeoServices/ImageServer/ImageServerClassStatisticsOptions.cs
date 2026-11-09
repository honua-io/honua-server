// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.GeoServices.ImageServer;

public sealed class ImageServerClassStatisticsOptions
{
    public const string SectionName = "GeoServices:ImageServer:ClassStatistics";

    [Range(1, 100_000_000)]
    public int MaxPixelsPerClass { get; set; } = 4_000_000;

    [Range(1, 4096)]
    public int MaxClasses { get; set; } = 64;
}
