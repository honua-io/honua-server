// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct RasterColormapEntry(double Value, byte Red, byte Green, byte Blue, byte Alpha);

public sealed class RasterColormap
{
    public required IReadOnlyList<RasterColormapEntry> Entries { get; init; }
}
