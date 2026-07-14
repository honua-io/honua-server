// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Tolerance-based coordinate comparisons shared by the 3D Tiles/I3S generation
/// and transcoding paths. <see cref="SceneVertex"/> longitude/latitude are
/// WGS-84 decimal degrees and height is ellipsoidal meters (see
/// <see cref="SceneVertex"/>); a ring's first and last vertex are expected to be
/// the identical physical point when a polygon is explicitly closed, but the two
/// values can arrive through different floating-point paths (parsing,
/// reprojection round-trips) and differ by a few ULPs even though they denote
/// the same location. Exact <c>==</c>/<c>!=</c> comparison on those values is
/// therefore unreliable; every ring-closure check below uses a tight, physically
/// meaningful epsilon instead.
/// </summary>
internal static class SceneVertexCoordinates
{
    // ~1e-9 decimal degrees is well under a millimeter at the equator: tight
    // enough to only fold together vertices that are the same physical point
    // recorded via slightly different floating-point paths, never distinct
    // input vertices that a producer intended to be different.
    private const double DegreesEpsilon = 1e-9;

    // Height is ellipsoidal meters; sub-micron tolerance is exactness for any
    // real-world elevation source while still absorbing floating-point noise.
    private const double HeightEpsilonMeters = 1e-6;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="a"/> and <paramref name="b"/>
    /// are the same decimal-degree coordinate within <see cref="DegreesEpsilon"/>.
    /// </summary>
    internal static bool DegreesEqual(double a, double b) => Math.Abs(a - b) <= DegreesEpsilon;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="first"/> and <paramref name="last"/>
    /// represent the same ring-closing vertex (longitude, latitude, and height
    /// all match within tolerance), so a duplicate closing vertex can be dropped.
    /// </summary>
    internal static bool IsRingClosingDuplicate(SceneVertex first, SceneVertex last) =>
        DegreesEqual(first.Longitude, last.Longitude)
        && DegreesEqual(first.Latitude, last.Latitude)
        && Math.Abs((first.Height ?? 0.0) - (last.Height ?? 0.0)) <= HeightEpsilonMeters;
}
