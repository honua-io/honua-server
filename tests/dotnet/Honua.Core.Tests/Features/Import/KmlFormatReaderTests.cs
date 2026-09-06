// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
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
        // honua-server#4406/#4419: this test asserted only Z, so a reader that swapped longitude
        // and latitude passed it. KML is lon,lat[,alt] while most XML geo formats are lat-first,
        // which makes transposition the characteristic silent-wrong-data failure for this format.
        point.X.Should().Be(-122.1);
        point.Y.Should().Be(37.1);
        point.Coordinate.Z.Should().Be(15);
        var line = result.Geometry.GetGeometryN(1).Should().BeOfType<LineString>().Subject;
        line.Coordinates[0].X.Should().Be(-122.2);
        line.Coordinates[0].Y.Should().Be(37.2);
        line.Coordinates[0].Z.Should().Be(20);
        line.Coordinates[1].X.Should().Be(-122.3);
        line.Coordinates[1].Y.Should().Be(37.3);
        line.Coordinates[1].Z.Should().Be(30);
    }

    /// <summary>
    /// A southern-hemisphere, western-longitude point whose |longitude| and |latitude| differ and
    /// whose signs differ: transposing the pair, dropping a sign, or reading the altitude into an
    /// ordinate all produce a different value here.
    /// </summary>
    [Fact]
    public async Task ReadStreamingAsync_PointCoordinates_AreLongitudeThenLatitude()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Ushuaia</name>
                  <Point><coordinates>-68.3029,-54.8019,7.5</coordinates></Point>
                </Placemark>
              </Document>
            </kml>
            """);

        var point = features.Should().ContainSingle().Subject.Geometry.Should().BeOfType<Point>().Subject;
        point.X.Should().Be(-68.3029, "KML coordinates are longitude first");
        point.Y.Should().Be(-54.8019, "KML coordinates are latitude second");
        point.Coordinate.Z.Should().Be(7.5);
    }

    /// <summary>
    /// Polygons, outer/inner boundary rings and ring closure had no coverage at all
    /// (honua-server#4419). The expected area is computed independently: a 0.2° x 0.2° outer
    /// square minus a 0.1° x 0.1° hole is 0.04 - 0.01 = 0.03 square degrees.
    /// </summary>
    [Fact]
    public async Task ReadStreamingAsync_PolygonWithHole_PreservesBothRingsAndTheirWinding()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>zone-with-hole</name>
                  <Polygon>
                    <outerBoundaryIs><LinearRing><coordinates>
                      -122.5,37.7 -122.3,37.7 -122.3,37.9 -122.5,37.9 -122.5,37.7
                    </coordinates></LinearRing></outerBoundaryIs>
                    <innerBoundaryIs><LinearRing><coordinates>
                      -122.45,37.75 -122.35,37.75 -122.35,37.85 -122.45,37.85 -122.45,37.75
                    </coordinates></LinearRing></innerBoundaryIs>
                  </Polygon>
                </Placemark>
              </Document>
            </kml>
            """);

        var polygon = features.Should().ContainSingle().Subject.Geometry.Should().BeOfType<Polygon>().Subject;
        polygon.NumInteriorRings.Should().Be(1, "the innerBoundaryIs ring must become a hole, not a second shell");
        polygon.ExteriorRing.Coordinates.Should().HaveCount(5);
        polygon.ExteriorRing.Coordinates[0].X.Should().Be(-122.5);
        polygon.ExteriorRing.Coordinates[0].Y.Should().Be(37.7);
        polygon.ExteriorRing.Coordinates[2].X.Should().Be(-122.3);
        polygon.ExteriorRing.Coordinates[2].Y.Should().Be(37.9);
        polygon.GetInteriorRingN(0).Coordinates[0].X.Should().Be(-122.45);
        polygon.GetInteriorRingN(0).Coordinates[0].Y.Should().Be(37.75);
        polygon.Area.Should().BeApproximately(0.03, 1e-12, "0.2 x 0.2 outer minus 0.1 x 0.1 hole");
        polygon.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// A ring whose coordinate list is not explicitly closed must be closed rather than dropped —
    /// the reader documents this behaviour but nothing asserted it.
    /// </summary>
    [Fact]
    public async Task ReadStreamingAsync_UnclosedRing_IsClosedRatherThanDiscarded()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <Polygon><outerBoundaryIs><LinearRing><coordinates>
                    -1,-1 1,-1 1,1 -1,1
                  </coordinates></LinearRing></outerBoundaryIs></Polygon>
                </Placemark>
              </Document>
            </kml>
            """);

        var polygon = features.Should().ContainSingle().Subject.Geometry.Should().BeOfType<Polygon>().Subject;
        polygon.ExteriorRing.IsClosed.Should().BeTrue();
        polygon.ExteriorRing.Coordinates[0].Should().Be(polygon.ExteriorRing.Coordinates[^1]);
        polygon.Area.Should().BeApproximately(4d, 1e-12, "a 2 x 2 square");
    }

    /// <summary>
    /// Placemarks nested in folders had no coverage, and neither did the feature count: an
    /// importer that dropped every placemark after the first passed the HTTP-level KML tests,
    /// which asserted only that the response mentioned the table name.
    /// </summary>
    [Fact]
    public async Task ReadStreamingAsync_PlacemarksInNestedFolders_YieldsEveryPlacemarkInOrder()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Folder>
                  <name>west</name>
                  <Placemark><name>first</name><Point><coordinates>-122.4194,37.7749</coordinates></Point></Placemark>
                  <Folder>
                    <name>west/inner</name>
                    <Placemark><name>second</name><Point><coordinates>-157.8583,21.3069</coordinates></Point></Placemark>
                  </Folder>
                </Folder>
                <Placemark><name>third</name><Point><coordinates>-68.3029,-54.8019</coordinates></Point></Placemark>
              </Document>
            </kml>
            """);

        features.Should().HaveCount(3);
        features.Select(feature => feature.Attributes["name"]).Should().Equal("first", "second", "third");
        var expected = new[] { (-122.4194, 37.7749), (-157.8583, 21.3069), (-68.3029, -54.8019) };
        for (var i = 0; i < expected.Length; i++)
        {
            var point = features[i].Geometry.Should().BeOfType<Point>().Subject;
            point.X.Should().Be(expected[i].Item1);
            point.Y.Should().Be(expected[i].Item2);
        }
    }

    private static async Task<List<NetTopologySuite.Features.IFeature>> ReadAllAsync(string kml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kml));
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in KmlFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }
}
