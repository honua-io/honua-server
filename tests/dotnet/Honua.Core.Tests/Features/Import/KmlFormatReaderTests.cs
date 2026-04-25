// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Import.Services;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.Import;

public sealed class KmlFormatReaderTests
{
    [Fact]
    public async Task ReadStreamingAsync_PreservesMultiGeometryExtendedDataAndAltitude()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Composite feature</name>
                  <ExtendedData>
                    <Data name="category"><value>survey</value></Data>
                    <SchemaData schemaUrl="#schema">
                      <SimpleData name="priority">high</SimpleData>
                    </SchemaData>
                  </ExtendedData>
                  <MultiGeometry>
                    <Point><coordinates>-122.1,37.1,15</coordinates></Point>
                    <LineString><coordinates>-122.2,37.2,20 -122.3,37.3,30</coordinates></LineString>
                  </MultiGeometry>
                </Placemark>
              </Document>
            </kml>
            """));

        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var parsedFeature in KmlFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(parsedFeature);
        }

        features.Should().ContainSingle();
        var result = features[0];
        result.Attributes["name"].Should().Be("Composite feature");
        result.Attributes["category"].Should().Be("survey");
        result.Attributes["priority"].Should().Be("high");

        result.Geometry.Should().BeOfType<GeometryCollection>();
        result.Geometry!.NumGeometries.Should().Be(2);
        var point = result.Geometry.GetGeometryN(0).Should().BeOfType<Point>().Subject;
        point.Coordinate.Z.Should().Be(15);
        var line = result.Geometry.GetGeometryN(1).Should().BeOfType<LineString>().Subject;
        line.Coordinates[0].Z.Should().Be(20);
        line.Coordinates[1].Z.Should().Be(30);
    }
}
