// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Bounding box for a map render operation.
/// Shared across rendering features (MapServer, StaticMap).
/// </summary>
internal readonly record struct RenderExtent(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>
    /// Width in map units.
    /// </summary>
    public double Width => MaxX - MinX;

    /// <summary>
    /// Height in map units.
    /// </summary>
    public double Height => MaxY - MinY;
}
