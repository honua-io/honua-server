// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

public sealed class TerrainRgbEncodingTests
{
    [UnitTest]
    public void Encode_NoDataValue_ReturnsBlackSentinel()
    {
        var pixel = TerrainRgbEncoding.Encode(TerrainRgbEncoding.NoDataSentinelMeters);

        pixel.Should().Be(new TerrainRgbPixel(0, 0, 0));
        TerrainRgbEncoding.Decode(pixel).Should().Be(TerrainRgbEncoding.NoDataSentinelMeters);
    }

    [UnitTest]
    public void Encode_ElevationMeters_RoundTripsToTenthMeter()
    {
        var pixel = TerrainRgbEncoding.Encode(123.4);

        TerrainRgbEncoding.Decode(pixel).Should().BeApproximately(123.4, 0.05);
    }

    [UnitTest]
    public void Encode_NullOrInvalidElevation_ReturnsBlackSentinel()
    {
        TerrainRgbEncoding.Encode(null).Should().Be(new TerrainRgbPixel(0, 0, 0));
        TerrainRgbEncoding.Encode(double.NaN).Should().Be(new TerrainRgbPixel(0, 0, 0));
        TerrainRgbEncoding.Encode(double.PositiveInfinity).Should().Be(new TerrainRgbPixel(0, 0, 0));
    }

    [UnitTest]
    public void Encode_AboveTerrainRgbRange_ClampsToMaximum()
    {
        var pixel = TerrainRgbEncoding.Encode(2_000_000);

        pixel.Should().Be(new TerrainRgbPixel(255, 255, 255));
        TerrainRgbEncoding.Decode(pixel).Should().Be(TerrainRgbEncoding.MaxElevationMeters);
    }
}
