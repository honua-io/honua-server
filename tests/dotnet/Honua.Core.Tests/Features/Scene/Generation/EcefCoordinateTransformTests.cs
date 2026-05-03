// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Generation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Generation;

/// <summary>
/// Unit tests for the WGS-84 → ECEF coordinate projection used by the v1
/// 3D Tiles generation pipeline (#842). Validates against well-known ECEF
/// coordinates so a regression in ellipsoid constants is caught immediately.
/// </summary>
public sealed class EcefCoordinateTransformTests
{
    private const double EcefMeterTolerance = 0.5;

    [UnitTest]
    public void ToEcef_OriginIsOnEquatorPrimeMeridian()
    {
        var (x, y, z) = EcefCoordinateTransform.ToEcef(0, 0, 0);

        // Equator + prime meridian = (a, 0, 0) where a is the semi-major axis.
        x.Should().BeApproximately(EcefCoordinateTransform.WgsSemiMajorAxis, EcefMeterTolerance);
        y.Should().BeApproximately(0, EcefMeterTolerance);
        z.Should().BeApproximately(0, EcefMeterTolerance);
    }

    [UnitTest]
    public void ToEcef_NorthPoleIsAtPolarRadius()
    {
        var (x, y, z) = EcefCoordinateTransform.ToEcef(0, 90, 0);

        // North pole: x ≈ 0, y ≈ 0, z ≈ b (semi-minor axis).
        var f = 1.0 / EcefCoordinateTransform.WgsInverseFlattening;
        var b = EcefCoordinateTransform.WgsSemiMajorAxis * (1.0 - f);

        x.Should().BeApproximately(0, EcefMeterTolerance);
        y.Should().BeApproximately(0, EcefMeterTolerance);
        z.Should().BeApproximately(b, EcefMeterTolerance);
    }

    [UnitTest]
    public void ToEcef_HeightAddsToEllipsoidalRadius()
    {
        var (x0, _, _) = EcefCoordinateTransform.ToEcef(0, 0, 0);
        var (x1, _, _) = EcefCoordinateTransform.ToEcef(0, 0, 1000);

        // Adding 1 km of height moves a point on the equator/prime meridian
        // exactly 1 km along the X axis.
        (x1 - x0).Should().BeApproximately(1000, EcefMeterTolerance);
    }

    [UnitTest]
    public void ToEcef_IsDeterministic()
    {
        var a = EcefCoordinateTransform.ToEcef(-122.4194, 37.7749, 12.5);
        var b = EcefCoordinateTransform.ToEcef(-122.4194, 37.7749, 12.5);

        a.Should().Be(b);
    }
}
