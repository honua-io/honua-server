// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OgcFeatures;

public sealed class OgcExtentTransformerTests
{
    [UnitTest]
    public void TryTransformToCrs84_WithWebMercatorOrigin_ReturnsLonLat()
    {
        var success = OgcExtentTransformer.TryTransformToCrs84(0d, 0d, 3857, out var coordinate);

        success.Should().BeTrue();
        coordinate.Lon.Should().BeApproximately(0d, 1e-9);
        coordinate.Lat.Should().BeApproximately(0d, 1e-9);
    }

    [UnitTest]
    public void TryTransformToCrs84_WithUnsupportedSrid_ReturnsFalse()
    {
        var success = OgcExtentTransformer.TryTransformToCrs84(500_000d, 4_100_000d, 26910, out var coordinate);

        success.Should().BeFalse();
        coordinate.Should().Be(default((double Lon, double Lat)));
    }

    [UnitTest]
    public void ToFeatureExtent_WithInvalidCrs_ThrowsArgumentException()
    {
        var extent = new SpatialExtent
        {
            BoundingBox = ImmutableArray.Create(ImmutableArray.Create(1d, 2d, 3d, 4d)),
            Crs = "invalid-crs"
        };

        var act = () => extent.ToFeatureExtent();

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void ToSrid_WithInvalidCrs_ThrowsFormatException()
    {
        var act = () => "invalid-crs".ToSrid();

        act.Should().Throw<FormatException>();
    }
}
