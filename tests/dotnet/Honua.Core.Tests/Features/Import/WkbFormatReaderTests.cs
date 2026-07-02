// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Features.Import;

// Regression coverage for honua-server#2353: a WKB payload (one or more concatenated WKB
// geometries) must import instead of failing with a generic "Multipart import request is invalid."
public sealed class WkbFormatReaderTests
{
    private static async Task<List<NetTopologySuite.Features.IFeature>> ReadAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in WkbFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }

    [Fact]
    public async Task ReadStreamingAsync_ConcatenatedWkbPoints_ReturnsGeometryFeatures()
    {
        var factory = new GeometryFactory();
        var writer = new WKBWriter();
        using var buffer = new MemoryStream();
        buffer.Write(writer.Write(factory.CreatePoint(new Coordinate(-156.30, 20.80))));
        buffer.Write(writer.Write(factory.CreatePoint(new Coordinate(-156.40, 20.90))));

        var features = await ReadAsync(buffer.ToArray());

        features.Should().HaveCount(2);
        features[0].Geometry.Should().BeOfType<Point>();
        features[0].Geometry!.Coordinate.X.Should().Be(-156.30);
        features[1].Geometry!.Coordinate.Y.Should().Be(20.90);
    }

    [Fact]
    public async Task ReadStreamingAsync_SingleWkbPolygon_ReturnsPolygon()
    {
        var factory = new GeometryFactory();
        var polygon = factory.CreatePolygon(new[]
        {
            new Coordinate(0, 0),
            new Coordinate(0, 10),
            new Coordinate(10, 10),
            new Coordinate(10, 0),
            new Coordinate(0, 0)
        });
        var bytes = new WKBWriter().Write(polygon);

        var features = await ReadAsync(bytes);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeAssignableTo<Polygon>();
        features[0].Geometry!.Area.Should().BeApproximately(100, 0.001);
    }
}
