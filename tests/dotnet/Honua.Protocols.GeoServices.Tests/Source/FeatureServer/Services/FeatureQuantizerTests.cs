// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Protocols.GeoServices.Tests.Source.FeatureServer.Services;

public sealed class FeatureQuantizerTests
{
    private const string UpperLeftJson =
        """{"mode":"view","originPosition":"upperLeft","tolerance":1,"extent":{"xmin":0,"ymin":0,"xmax":100,"ymax":100}}""";

    private static QuantizationTransform Parse(string json)
    {
        FeatureQuantizer.TryParse(json, out var transform, out var error).Should().BeTrue(error);
        transform.Should().NotBeNull();
        return transform!;
    }

    [UnitTest]
    public void TryParse_Absent_ReturnsNullTransform()
    {
        FeatureQuantizer.TryParse(null, out var transform, out var error).Should().BeTrue();
        transform.Should().BeNull();
        error.Should().BeNull();
    }

    [UnitTest]
    public void TryParse_UpperLeft_AnchorsTranslateAtExtentTopLeft()
    {
        var transform = Parse(
            """{"originPosition":"upperLeft","tolerance":1.5,"extent":{"xmin":10,"ymin":20,"xmax":110,"ymax":120}}""");

        transform.OriginPosition.Should().Be(QuantizationTransform.UpperLeft);
        transform.ScaleX.Should().Be(1.5);
        transform.ScaleY.Should().Be(1.5);
        transform.TranslateX.Should().Be(10);
        transform.TranslateY.Should().Be(120); // ymax for upperLeft
    }

    [UnitTest]
    public void TryParse_LowerLeft_AnchorsTranslateAtExtentBottomLeft()
    {
        var transform = Parse(
            """{"originPosition":"lowerLeft","tolerance":2,"extent":{"xmin":10,"ymin":20,"xmax":110,"ymax":120}}""");

        transform.OriginPosition.Should().Be(QuantizationTransform.LowerLeft);
        transform.TranslateY.Should().Be(20); // ymin for lowerLeft
    }

    [UnitTest]
    public void TryParse_MissingExtent_ReturnsError()
    {
        FeatureQuantizer.TryParse("""{"tolerance":1}""", out var transform, out var error).Should().BeFalse();
        transform.Should().BeNull();
        error.Should().Contain("extent");
    }

    [UnitTest]
    public void TryParse_NonPositiveTolerance_ReturnsError()
    {
        FeatureQuantizer.TryParse(
            """{"tolerance":0,"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}""",
            out _, out var error).Should().BeFalse();
        error.Should().Contain("tolerance");
    }

    [UnitTest]
    public void TryParse_EditMode_ReturnsError()
    {
        FeatureQuantizer.TryParse(
            """{"mode":"edit","tolerance":1,"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}""",
            out _, out var error).Should().BeFalse();
        error.Should().Contain("mode");
    }

    [UnitTest]
    public void TryParse_Malformed_ReturnsError()
    {
        FeatureQuantizer.TryParse("not json", out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [UnitTest]
    public void Quantize_Point_MapsToIntegerGrid()
    {
        var transform = Parse(
            """{"tolerance":2,"extent":{"xmin":0,"ymin":0,"xmax":100,"ymax":100}}""");

        var quantized = FeatureQuantizer.Quantize(new GeoServicesGeometry { X = 10, Y = 90 }, transform);

        quantized.X.Should().Be(5); // (10-0)/2
        quantized.Y.Should().Be(5); // (100-90)/2 upperLeft
    }

    [UnitTest]
    public void Quantize_Ring_DeltaEncodesFirstAbsoluteThenDeltas()
    {
        var transform = Parse(UpperLeftJson);
        var geometry = new GeoServicesGeometry
        {
            Rings =
            [
                [[10, 90], [20, 90], [20, 80], [10, 90]],
            ],
        };

        var quantized = FeatureQuantizer.Quantize(geometry, transform);

        var ring = quantized.Rings![0];
        ring[0].Should().Equal(10, 10);   // absolute: (10-0)/1, (100-90)/1
        ring[1].Should().Equal(10, 0);    // delta to (20,90)
        ring[2].Should().Equal(0, 10);    // delta to (20,80)
        ring[3].Should().Equal(-10, -10); // delta back to (10,90)
    }

    [UnitTest]
    public void Quantize_Ring_RoundTripsWithinTolerance()
    {
        var transform = Parse(
            """{"tolerance":0.5,"extent":{"xmin":-122.7,"ymin":37.3,"xmax":-121.9,"ymax":37.8}}""");
        double[][] originalRing =
        [
            [-122.6, 37.7], [-122.0, 37.75], [-122.1, 37.4], [-122.6, 37.7],
        ];
        var geometry = new GeoServicesGeometry { Rings = [originalRing] };

        var quantized = FeatureQuantizer.Quantize(geometry, transform);
        var decoded = Dequantize(quantized.Rings![0], transform);

        for (var i = 0; i < originalRing.Length; i++)
        {
            decoded[i][0].Should().BeApproximately(originalRing[i][0], transform.ScaleX);
            decoded[i][1].Should().BeApproximately(originalRing[i][1], transform.ScaleY);
        }
    }

    [UnitTest]
    public void Quantize_EnvelopeAndNull_PassThroughUnchanged()
    {
        var transform = Parse(UpperLeftJson);
        var envelope = new GeoServicesGeometry { Xmin = 0, Ymin = 0, Xmax = 10, Ymax = 10 };

        var quantized = FeatureQuantizer.Quantize(envelope, transform);

        quantized.Xmin.Should().Be(0);
        quantized.Xmax.Should().Be(10);
        quantized.Rings.Should().BeNull();
    }

    // Reconstructs world coordinates from a delta-encoded quantized ring.
    private static double[][] Dequantize(double[][] ring, QuantizationTransform transform)
    {
        var decoded = new double[ring.Length][];
        long qx = 0;
        long qy = 0;
        for (var i = 0; i < ring.Length; i++)
        {
            qx = i == 0 ? (long)ring[i][0] : qx + (long)ring[i][0];
            qy = i == 0 ? (long)ring[i][1] : qy + (long)ring[i][1];
            var worldX = transform.TranslateX + (qx * transform.ScaleX);
            var worldY = transform.OriginPosition == QuantizationTransform.LowerLeft
                ? transform.TranslateY + (qy * transform.ScaleY)
                : transform.TranslateY - (qy * transform.ScaleY);
            decoded[i] = [worldX, worldY];
        }

        return decoded;
    }
}
