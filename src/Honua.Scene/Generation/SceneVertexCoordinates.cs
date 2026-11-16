// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

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

    internal static bool DegreesEqual(double a, double b) => Math.Abs(a - b) <= DegreesEpsilon;

    internal static bool IsRingClosingDuplicate(SceneVertex first, SceneVertex last) =>
        DegreesEqual(first.Longitude, last.Longitude)
        && DegreesEqual(first.Latitude, last.Latitude)
        && Math.Abs((first.Height ?? 0.0) - (last.Height ?? 0.0)) <= HeightEpsilonMeters;
}
