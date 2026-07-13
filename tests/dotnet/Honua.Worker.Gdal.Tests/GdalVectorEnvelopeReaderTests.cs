// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Unit coverage for <see cref="GdalVectorEnvelopeReader"/> — the cheap GeoJSON extent
/// read that feeds the rasterize <c>-tr</c> output bound (#2793). Proves the axis-aligned
/// envelope is recovered across geometry nesting and that unparseable / position-free
/// payloads report "no envelope" so the executor admits and falls back to the input caps.
/// </summary>
public sealed class GdalVectorEnvelopeReaderTests
{
    private static bool TryRead(string json, out GdalVectorEnvelopeReader.Envelope envelope)
        => GdalVectorEnvelopeReader.TryReadEnvelope(Encoding.UTF8.GetBytes(json), out envelope);

    [UnitTest]
    public void TryReadEnvelope_Polygon_RecoversExtent()
    {
        const string json =
            "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{},"
            + "\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[-5,-2],[10,-2],[10,8],[-5,8],[-5,-2]]]}}]}";

        TryRead(json, out var env).Should().BeTrue();
        env.MinX.Should().Be(-5);
        env.MinY.Should().Be(-2);
        env.MaxX.Should().Be(10);
        env.MaxY.Should().Be(8);
        env.Width.Should().Be(15);
        env.Height.Should().Be(10);
    }

    [UnitTest]
    public void TryReadEnvelope_MultiFeaturePointsAndLines_UnionsAllPositions()
    {
        const string json =
            "{\"type\":\"FeatureCollection\",\"features\":["
            + "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,1]}},"
            + "{\"type\":\"Feature\",\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[3,4],[-2,20]]}}]}";

        TryRead(json, out var env).Should().BeTrue();
        env.MinX.Should().Be(-2);
        env.MinY.Should().Be(1);
        env.MaxX.Should().Be(3);
        env.MaxY.Should().Be(20);
    }

    [UnitTest]
    public void TryReadEnvelope_IgnoresElevationOrdinate()
    {
        const string json =
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[100,50,9999]}}";

        TryRead(json, out var env).Should().BeTrue();
        env.MinX.Should().Be(100);
        env.MaxX.Should().Be(100);
        env.MinY.Should().Be(50);
        env.MaxY.Should().Be(50);
    }

    [UnitTest]
    public void TryReadEnvelope_BBox_IsFolded()
    {
        const string json =
            "{\"type\":\"FeatureCollection\",\"bbox\":[-10,-20,30,40],\"features\":[]}";

        TryRead(json, out var env).Should().BeTrue();
        env.MinX.Should().Be(-10);
        env.MinY.Should().Be(-20);
        env.MaxX.Should().Be(30);
        env.MaxY.Should().Be(40);
    }

    [UnitTest]
    public void TryReadEnvelope_EmptyFeatureCollection_ReturnsFalse()
    {
        TryRead("{\"type\":\"FeatureCollection\",\"features\":[]}", out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryReadEnvelope_NotJson_ReturnsFalse()
    {
        TryRead("this is not json", out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryReadEnvelope_IgnoresNumericPropertiesOutsideCoordinates()
    {
        // A giant attribute value under a normal property must NOT widen the envelope.
        const string json =
            "{\"type\":\"Feature\",\"properties\":{\"population\":999999999},"
            + "\"geometry\":{\"type\":\"Point\",\"coordinates\":[2,3]}}";

        TryRead(json, out var env).Should().BeTrue();
        env.MaxX.Should().Be(2);
        env.MaxY.Should().Be(3);
    }
}
