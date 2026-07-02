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
