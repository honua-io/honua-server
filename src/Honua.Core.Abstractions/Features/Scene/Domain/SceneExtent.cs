// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// WGS-84 axis-aligned bounding box for a scene dataset, in decimal degrees,
/// with an optional vertical (elevation) range in metres.
/// </summary>
/// <param name="XMin">Minimum longitude (-180 to 180).</param>
/// <param name="YMin">Minimum latitude (-90 to 90).</param>
/// <param name="XMax">Maximum longitude (-180 to 180).</param>
/// <param name="YMax">Maximum latitude (-90 to 90).</param>
/// <param name="ZMin">
/// Optional minimum elevation in metres. Null when the vertical range is not
/// known (e.g. a 2D-only registration). When present it carries the lower
/// bound read from the tileset's root bounding volume.
/// </param>
/// <param name="ZMax">
/// Optional maximum elevation in metres. Null when the vertical range is not
/// known. When present it carries the upper bound read from the tileset's
/// root bounding volume.
/// </param>
/// <remarks>
/// The horizontal envelope is the persisted 2D bounding box. The optional
/// vertical range lets a z-bearing source (the 3D Tiles root
/// <c>boundingVolume.region</c> min/max height) thread a true 3D extent into
/// the I3S <c>fullExtent</c> without fabricating a 0..0 vertical range when no
/// height source exists.
/// </remarks>
public sealed record SceneExtent(
    double XMin,
    double YMin,
    double XMax,
    double YMax,
    double? ZMin = null,
    double? ZMax = null);
