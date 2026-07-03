// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.Import;

// Regression coverage for honua-server#2354: GPX produced by GDAL (ogr2ogr -f GPX
// -dsco GPX_USE_EXTENSIONS=YES) stores source attributes under <extensions> with child
// elements. The reader previously threw XmlException ("ReadElementContentAs() methods cannot
// be called on an element that has child elements") and yielded no features.
public sealed class GpxFormatReaderTests
{
    private static async Task<List<NetTopologySuite.Features.IFeature>> ReadAsync(string gpx)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx));
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in GpxFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }

    [Fact]
    public async Task ReadStreamingAsync_GdalWaypointsWithExtensions_ReturnsFeaturesWithAttributes()
    {
        const string gpx = """
            <?xml version="1.0"?>
            <gpx version="1.1" creator="GDAL" xmlns:ogr="http://osgeo.org/gdal" xmlns="http://www.topografix.com/GPX/1/1">
            <metadata><bounds minlat="20.8" minlon="-156.4" maxlat="20.9" maxlon="-156.3"/></metadata>
            <wpt lat="20.8" lon="-156.3">
              <extensions>
                <ogr:zone_code>030</ogr:zone_code>
                <ogr:zone_name>Residential</ogr:zone_name>
              </extensions>
            </wpt>
            <wpt lat="20.9" lon="-156.4">
              <extensions>
                <ogr:zone_code>500</ogr:zone_code>
                <ogr:zone_name>Commercial</ogr:zone_name>
              </extensions>
            </wpt>
            </gpx>
            """;

        var features = await ReadAsync(gpx);

        features.Should().HaveCount(2);

        var first = features[0];
        first.Geometry.Should().BeOfType<Point>();
        first.Geometry!.Coordinate.X.Should().Be(-156.3);
        first.Geometry!.Coordinate.Y.Should().Be(20.8);
        first.Attributes["zone_code"].Should().Be("030");
        first.Attributes["zone_name"].Should().Be("Residential");

        features[1].Attributes["zone_code"].Should().Be("500");
        features[1].Attributes["zone_name"].Should().Be("Commercial");
    }

    [Fact]
    public async Task ReadStreamingAsync_RouteWithNameAndTwoPoints_ReturnsSingleLineString()
    {
        // #2354: a <name> preceding the <rtept> points made the reader over-advance and drop the
        // first point; a two-point route then fell below the 2-coordinate minimum and yielded
        // zero features (silent data loss).
        const string gpx = """
            <?xml version="1.0"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
            <rte>
              <name>Route 1</name>
              <rtept lat="1.0" lon="2.0"/>
              <rtept lat="3.0" lon="4.0"/>
            </rte>
            </gpx>
            """;

        var features = await ReadAsync(gpx);

        features.Should().ContainSingle();
        var line = features[0].Geometry.Should().BeOfType<LineString>().Subject;
        line.Coordinates.Should().HaveCount(2);
        line.Coordinates[0].X.Should().Be(2.0);
        line.Coordinates[0].Y.Should().Be(1.0);
        line.Coordinates[1].X.Should().Be(4.0);
        features[0].Attributes["name"].Should().Be("Route 1");
    }

    [Fact]
    public async Task ReadStreamingAsync_TrackWithNameAndSegments_ReturnsSingleLineString()
    {
        // #2354: tracks must collect trkpt points across <trkseg> nesting even when a <name>
        // precedes them, and even when trkpt carries child elements (<ele>).
        const string gpx = """
            <?xml version="1.0"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
            <trk>
              <name>Track 1</name>
              <trkseg>
                <trkpt lat="1.0" lon="2.0"><ele>10</ele></trkpt>
                <trkpt lat="3.0" lon="4.0"><ele>20</ele></trkpt>
              </trkseg>
              <trkseg>
                <trkpt lat="5.0" lon="6.0"/>
              </trkseg>
            </trk>
            </gpx>
            """;

        var features = await ReadAsync(gpx);

        features.Should().ContainSingle();
        var line = features[0].Geometry.Should().BeOfType<LineString>().Subject;
        line.Coordinates.Should().HaveCount(3);
        features[0].Attributes["name"].Should().Be("Track 1");
    }

    [Fact]
    public async Task ReadStreamingAsync_MixedWaypointsRoutesTracks_ReturnsAllFeatures()
    {
        // #2354: a document combining all three GPX geometry kinds must emit one feature each.
        const string gpx = """
            <?xml version="1.0"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
            <wpt lat="10.0" lon="20.0"><name>WP</name></wpt>
            <rte>
              <name>R</name>
              <rtept lat="1.0" lon="2.0"/>
              <rtept lat="3.0" lon="4.0"/>
            </rte>
            <trk>
              <name>T</name>
              <trkseg>
                <trkpt lat="5.0" lon="6.0"/>
                <trkpt lat="7.0" lon="8.0"/>
              </trkseg>
            </trk>
            </gpx>
            """;

        var features = await ReadAsync(gpx);

        features.Should().HaveCount(3);
        features.Select(f => f.Geometry!.GeometryType).Should()
            .BeEquivalentTo(["Point", "LineString", "LineString"]);
    }

    [Fact]
    public async Task ReadStreamingAsync_SimpleWaypointChildren_ReturnsAttributes()
    {
        const string gpx = """
            <?xml version="1.0"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
            <wpt lat="10.0" lon="20.0">
              <name>Summit</name>
              <ele>1234</ele>
            </wpt>
            </gpx>
            """;

        var features = await ReadAsync(gpx);

        features.Should().ContainSingle();
        features[0].Attributes["name"].Should().Be("Summit");
        features[0].Attributes["ele"].Should().Be("1234");
    }
}
