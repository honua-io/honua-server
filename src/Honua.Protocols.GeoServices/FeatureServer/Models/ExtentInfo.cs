// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Spatial extent information
/// </summary>
public sealed class ExtentInfo
{
    /// <summary>
    /// Minimum X coordinate
    /// </summary>
    public required double Xmin { get; init; }

    /// <summary>
    /// Minimum Y coordinate
    /// </summary>
    public required double Ymin { get; init; }

    /// <summary>
    /// Maximum X coordinate
    /// </summary>
    public required double Xmax { get; init; }

    /// <summary>
    /// Maximum Y coordinate
    /// </summary>
    public required double Ymax { get; init; }

    /// <summary>
    /// Spatial reference for the extent
    /// </summary>
    public required SpatialReferenceInfo SpatialReference { get; init; }

    /// <summary>
    /// Creates ExtentInfo from a unified BoundingBox
    /// </summary>
    /// <param name="boundingBox">Unified bounding box</param>
    /// <param name="spatialReference">Spatial reference system</param>
    /// <returns>ExtentInfo instance</returns>
    public static ExtentInfo FromBoundingBox(BoundingBox boundingBox, SpatialReference? spatialReference = null)
        => new()
        {
            Xmin = boundingBox.MinX,
            Ymin = boundingBox.MinY,
            Xmax = boundingBox.MaxX,
            Ymax = boundingBox.MaxY,
            SpatialReference = (spatialReference ?? (boundingBox.SpatialReferenceId.HasValue
                ? Honua.Core.Features.Shared.Models.SpatialReference.Create(boundingBox.SpatialReferenceId.Value)
                : Honua.Core.Features.Shared.Models.SpatialReference.WGS84)).ToSpatialReferenceInfo()
        };

    /// <summary>
    /// Converts this ExtentInfo to a unified BoundingBox
    /// </summary>
    /// <returns>Unified BoundingBox</returns>
    public BoundingBox ToBoundingBox()
        => BoundingBox.Create(Xmin, Ymin, Xmax, Ymax, SpatialReference.Wkid);
}
