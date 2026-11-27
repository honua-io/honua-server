// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Features.Import;

// A WKB payload (one or more concatenated WKB geometries) must import instead of failing with a
// generic "Multipart import request is invalid."
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_PayloadExceedingMaxMemoryBytes_ThrowsInvalidDataException()
    {
        var bytes = new WKBWriter().Write(new GeometryFactory().CreatePoint(new Coordinate(1, 2)));
        var limits = new ImportLimits { MaxMemoryBytes = bytes.Length - 1 };

        var act = async () =>
        {
            await using var stream = new MemoryStream(bytes);
            await foreach (var feature in WkbFormatReader.ReadStreamingAsync(stream, limits, CancellationToken.None))
            {
                _ = feature;
            }
        };

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*maximum buffered size*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_SingleGeometryExceedingMaxWkbSize_ThrowsInvalidDataException()
    {
        var bytes = new WKBWriter().Write(new GeometryFactory().CreatePoint(new Coordinate(1, 2)));
        var limits = new ImportLimits { MaxMemoryBytes = 1024, MaxWkbSize = bytes.Length - 1 };

        var act = async () =>
        {
            await using var stream = new MemoryStream(bytes);
            await foreach (var feature in WkbFormatReader.ReadStreamingAsync(stream, limits, CancellationToken.None))
            {
                _ = feature;
            }
        };

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*WKB geometry size*");
    }
}
