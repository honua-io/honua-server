// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Features.Import;

public sealed class WktFormatReaderTests
{
    [Fact]
    public async Task ReadStreamingAsync_PlainWkt_ParsesFeatures()
    {
        var features = await ReadAllAsync("""
            POINT(-122.4194 37.7749)
            LINESTRING(0 0, 1 1)
            """);

        features.Should().HaveCount(2);
        features[0].Geometry.Should().BeOfType<Point>();
        features[1].Geometry.Should().BeOfType<LineString>();
    }

    [Fact]
    public async Task ReadStreamingAsync_EwktWithSridPrefix_ParsesFeatureAndHonoursSrid()
    {
        // #2363: a .wkt file exported with the standard PostGIS "SRID=<n>;" prefix previously
        // yielded zero features because the bare WKTReader cannot parse EWKT.
        var features = await ReadAllAsync("SRID=4326;POINT(-122.4194 37.7749)");

        features.Should().ContainSingle();
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        point.SRID.Should().Be(4326);
        point.X.Should().BeApproximately(-122.4194, 1e-6);
    }

    [Fact]
    public async Task ReadStreamingAsync_MultiLineGeometry_ParsesSingleFeature()
    {
        // #2363: a single geometry pretty-printed across several lines must be accumulated
        // before parsing rather than parsed (and dropped) line-by-line.
        var features = await ReadAllAsync("""
            POLYGON((
                0 0,
                0 1,
                1 1,
                1 0,
                0 0
            ))
            """);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeOfType<Polygon>();
        features[0].Geometry!.Coordinates.Should().HaveCount(5);
    }

    [Fact]
    public async Task ReadStreamingAsync_WkbHexLine_ParsesFeature()
    {
        var expected = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var writer = new WKBWriter { HandleSRID = true };
        var hex = Convert.ToHexString(writer.Write(expected));

        var features = await ReadAllAsync(hex);

        features.Should().ContainSingle();
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        point.X.Should().BeApproximately(-122.4194, 1e-6);
        point.SRID.Should().Be(4326);
    }

    private static async Task<List<IFeature>> ReadAllAsync(string content)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var features = new List<IFeature>();
        await foreach (var feature in WktFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }
}
