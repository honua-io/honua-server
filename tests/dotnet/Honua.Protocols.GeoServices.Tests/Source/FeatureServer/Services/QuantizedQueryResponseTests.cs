// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Protocols.GeoServices.Tests.Source.FeatureServer.Services;

/// <summary>
/// Wire-level tests for view-mode quantized <c>f=json</c> featureSets (#5438). A client
/// reads the featureSet front to back and resolves each quantized geometry against the
/// <c>transform</c> it has already read, so the transform must be serialized before
/// <c>features</c>; otherwise the integer coordinates are never dequantized and nothing draws.
/// </summary>
public sealed class QuantizedQueryResponseTests
{
    // The polygon exchange from #5438: two clockwise fixture rectangles requested with
    // view-mode, lowerLeft quantization at a 1.65e-5 degree tolerance.
    private const string PolygonQuantizationJson =
        """{"mode":"view","originPosition":"lowerLeft","tolerance":1.6527810833389295e-05,"extent":{"xmin":-122.43401947248334,"ymin":37.76498053451662,"xmax":-122.40598,"ymax":37.78602,"spatialReference":{"wkid":4326}}}""";

    // The point exchange from #5438: the test layer's four located points requested with
    // view-mode, lowerLeft quantization at a 0.001 degree tolerance.
    private const string PointQuantizationJson =
        """{"mode":"view","originPosition":"lowerLeft","tolerance":0.0010000020000040025,"extent":{"xmin":-122.80687566301286,"ymin":37.1681245869876307,"xmax":-121.793124336987134,"ymax":37.9318754130123708,"spatialReference":{"wkid":4326,"latestWkid":4326}}}""";

    [UnitTest]
    public void Apply_PolygonLayer_SerializesTransformBeforeFeaturesAndDecodesToFixtureRings()
    {
        var transform = Parse(PolygonQuantizationJson);
        var first = Rectangle(transform, 909, 667, 363, 302);
        var second = Rectangle(transform, 425, 304, 363, 302);
        var response = new QueryResponse
        {
            GeometryType = "esriGeometryPolygon",
            SpatialReference = new GeoServicesSpatialReference { Wkid = 4326, LatestWkid = 4326 },
            ObjectIdFieldName = "objectid",
            DisplayFieldName = "objectid",
            Features =
            [
                PolygonFeature(300218, first),
                PolygonFeature(300219, second),
            ],
        };

        var decoded = DecodeAsStreamingClient(Serialize(FeatureQuantizer.Apply(response, transform)));

        decoded.Should().HaveCount(2);
        AssertRingsEqual(decoded[0].Rings!, [first], transform.ScaleX);
        AssertRingsEqual(decoded[1].Rings!, [second], transform.ScaleX);
    }

    [UnitTest]
    public void Apply_PointLayer_SerializesTransformBeforeFeaturesAndDecodesToFixturePoints()
    {
        var transform = Parse(PointQuantizationJson);
        (double X, double Y)[] points = [(-122.5, 37.5), (-122.7, 37.7), (-121.9, 37.3), (-122.3, 37.8)];
        var response = new QueryResponse
        {
            GeometryType = "esriGeometryPoint",
            SpatialReference = new GeoServicesSpatialReference { Wkid = 4326, LatestWkid = 4326 },
            ObjectIdFieldName = "objectid",
            DisplayFieldName = "objectid",
            Features = points
                .Select((point, index) => new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?> { ["objectid"] = (long)index + 1 },
                    Geometry = new GeoServicesGeometry { X = point.X, Y = point.Y },
                })
                .ToArray(),
        };

        var decoded = DecodeAsStreamingClient(Serialize(FeatureQuantizer.Apply(response, transform)));

        decoded.Should().HaveCount(points.Length);
        for (var i = 0; i < points.Length; i++)
        {
            decoded[i].X.Should().BeApproximately(points[i].X, transform.ScaleX / 2);
            decoded[i].Y.Should().BeApproximately(points[i].Y, transform.ScaleY / 2);
        }
    }

    private static QuantizationTransform Parse(string json)
    {
        FeatureQuantizer.TryParse(json, out var transform, out var error).Should().BeTrue(error);
        return transform!;
    }

    // A clockwise (y-up) closed rectangle whose corners sit on the quantization grid.
    private static double[][] Rectangle(QuantizationTransform transform, long qx, long qy, long width, long height)
    {
        double X(long q) => transform.TranslateX + (q * transform.ScaleX);
        double Y(long q) => transform.TranslateY + (q * transform.ScaleY);
        return
        [
            [X(qx), Y(qy)],
            [X(qx), Y(qy + height)],
            [X(qx + width), Y(qy + height)],
            [X(qx + width), Y(qy)],
            [X(qx), Y(qy)],
        ];
    }

    private static GeoServicesFeature PolygonFeature(long objectId, double[][] ring)
        => new()
        {
            Attributes = new Dictionary<string, object?> { ["objectid"] = objectId },
            Geometry = new GeoServicesGeometry { Rings = [ring] },
        };

    private static string Serialize(QueryResponse response)
        => JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.QueryResponse);

    private sealed record DecodedGeometry(double X, double Y, double[][][]? Rings);

    // Reads the top-level members in wire order, as a streaming client does: geometry is
    // dequantized with the transform read so far, so a transform that arrives after
    // features leaves the integer coordinates undecodable.
    private static List<DecodedGeometry> DecodeAsStreamingClient(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement? transform = null;
        List<DecodedGeometry>? decoded = null;
        foreach (var member in document.RootElement.EnumerateObject())
        {
            if (member.NameEquals("transform"))
            {
                transform = member.Value.Clone();
            }
            else if (member.NameEquals("features"))
            {
                transform.Should().NotBeNull(
                    "the quantization transform must be serialized before features so a streaming client can dequantize them: {0}",
                    json);
                var readTransform = transform.GetValueOrDefault();
                decoded = member.Value.EnumerateArray()
                    .Select(feature => Dequantize(feature.GetProperty("geometry"), readTransform))
                    .ToList();
            }
        }

        return decoded ?? throw new InvalidOperationException($"The featureSet carries no features: {json}");
    }

    private static DecodedGeometry Dequantize(JsonElement geometry, JsonElement transform)
    {
        var lowerLeft = transform.GetProperty("originPosition").GetString() == QuantizationTransform.LowerLeft;
        var scale = transform.GetProperty("scale");
        var translate = transform.GetProperty("translate");
        var scaleX = scale[0].GetDouble();
        var scaleY = scale[1].GetDouble();
        var translateX = translate[0].GetDouble();
        var translateY = translate[1].GetDouble();
        double WorldX(long q) => translateX + (q * scaleX);
        double WorldY(long q) => lowerLeft ? translateY + (q * scaleY) : translateY - (q * scaleY);

        if (geometry.TryGetProperty("rings", out var rings))
        {
            var decodedRings = rings.EnumerateArray()
                .Select(ring =>
                {
                    long qx = 0;
                    long qy = 0;
                    return ring.EnumerateArray()
                        .Select(vertex =>
                        {
                            // Rings and paths are delta-encoded: the first vertex is relative to
                            // the origin, every later vertex to its predecessor.
                            qx += vertex[0].GetInt64();
                            qy += vertex[1].GetInt64();
                            return new[] { WorldX(qx), WorldY(qy) };
                        })
                        .ToArray();
                })
                .ToArray();
            return new DecodedGeometry(double.NaN, double.NaN, decodedRings);
        }

        return new DecodedGeometry(
            WorldX(geometry.GetProperty("x").GetInt64()),
            WorldY(geometry.GetProperty("y").GetInt64()),
            null);
    }

    private static void AssertRingsEqual(double[][][] actual, double[][][] expected, double tolerance)
    {
        actual.Should().HaveCount(expected.Length);
        for (var ring = 0; ring < expected.Length; ring++)
        {
            actual[ring].Should().HaveCount(expected[ring].Length);
            for (var vertex = 0; vertex < expected[ring].Length; vertex++)
            {
                actual[ring][vertex][0].Should().BeApproximately(expected[ring][vertex][0], tolerance / 2);
                actual[ring][vertex][1].Should().BeApproximately(expected[ring][vertex][1], tolerance / 2);
            }
        }
    }
}
