// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>
/// Represents a bounding box.
/// </summary>
public sealed class Extent
{
    /// <summary>Minimum X coordinate.</summary>
    public double Xmin { get; init; }

    /// <summary>Minimum Y coordinate.</summary>
    public double Ymin { get; init; }

    /// <summary>Maximum X coordinate.</summary>
    public double Xmax { get; init; }

    /// <summary>Maximum Y coordinate.</summary>
    public double Ymax { get; init; }

    /// <summary>Spatial reference.</summary>
    public SpatialReference? SpatialReference { get; init; }
}
