// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.Import;

public sealed class GmlFormatReaderTests
{
    // -----------------------------------------------------------------------
    // ParseSrsNameToSrid
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("EPSG:4326", 4326)]
    [InlineData("urn:ogc:def:crs:EPSG::4326", 4326)]
    [InlineData("urn:ogc:def:crs:EPSG::25832", 25832)]
    [InlineData("http://www.opengis.net/gml/srs/epsg.xml#4326", 4326)]
    [InlineData("CRS84", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseSrsNameToSrid_ReturnsExpectedSrid(string? srsName, int? expected)
    {
        var result = GmlFormatReader.ParseSrsNameToSrid(srsName);
        result.Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // GML 3 FeatureCollection — basic geometry types
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadStreamingAsync_Gml3Point_ReturnsPointFeature()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml"
              xmlns:sf="http://example.com/sf"
              srsName="EPSG:4326">
              <gml:featureMember>
                <sf:Road>
                  <sf:name>Main St</sf:name>
                  <sf:geometry>
                    <gml:Point>
                      <gml:pos>-122.1 37.5</gml:pos>
                    </gml:Point>
                  </sf:geometry>
                </sf:Road>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """;

        var features = await CollectAsync(gml);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeOfType<Point>();
        var point = (Point)features[0].Geometry!;
        point.X.Should().BeApproximately(-122.1, 0.0001);
        point.Y.Should().BeApproximately(37.5, 0.0001);
        features[0].Attributes["name"].Should().Be("Main St");
    }

    [Fact]
    public async Task ReadStreamingAsync_Gml3LineString_ReturnsLineStringFeature()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml"
              xmlns:sf="http://example.com/sf">
              <gml:featureMember>
                <sf:Road>
                  <sf:name>Highway 1</sf:name>
                  <sf:geom>
                    <gml:LineString srsName="EPSG:4326">
                      <gml:posList>-122.1 37.5 -122.2 37.6 -122.3 37.7</gml:posList>
                    </gml:LineString>
                  </sf:geom>
                </sf:Road>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """;

        var features = await CollectAsync(gml);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeOfType<LineString>();
        var line = (LineString)features[0].Geometry!;
        line.NumPoints.Should().Be(3);
        features[0].Attributes["name"].Should().Be("Highway 1");
    }

    [Fact]
    public async Task ReadStreamingAsync_Gml3Polygon_ReturnsPolygonFeature()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml"
              xmlns:sf="http://example.com/sf">
              <gml:featureMember>
                <sf:Parcel>
                  <sf:parcelId>P-001</sf:parcelId>
                  <sf:shape>
                    <gml:Polygon srsName="EPSG:4326">
                      <gml:exterior>
                        <gml:LinearRing>
                          <gml:posList>0 0 0 1 1 1 1 0 0 0</gml:posList>
                        </gml:LinearRing>
                      </gml:exterior>
                    </gml:Polygon>
                  </sf:shape>
                </sf:Parcel>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """;

        var features = await CollectAsync(gml);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeOfType<Polygon>();
        features[0].Attributes["parcelId"].Should().Be("P-001");
    }

    [Fact]
    public async Task ReadStreamingAsync_Gml3PolygonWithHole_PreservesInteriorRing()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml"
              xmlns:sf="http://example.com/sf">
              <gml:featureMember>
                <sf:Zone>
                  <sf:geom>
                    <gml:Polygon>
                      <gml:exterior>
                        <gml:LinearRing>
                          <gml:posList>0 0 0 10 10 10 10 0 0 0</gml:posList>
                        </gml:LinearRing>
                      </gml:exterior>
                      <gml:interior>
                        <gml:LinearRing>
                          <gml:posList>2 2 2 4 4 4 4 2 2 2</gml:posList>
                        </gml:LinearRing>
                      </gml:interior>
                    </gml:Polygon>
                  </sf:geom>
                </sf:Zone>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """;

        var features = await CollectAsync(gml);

        features.Should().ContainSingle();
        var polygon = features[0].Geometry.Should().BeOfType<Polygon>().Subject;
        polygon.NumInteriorRings.Should().Be(1);
    }

    [Fact]
    public async Task ReadStreamingAsync_MultipleFeatureMembers_ReturnsAllFeatures()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml"
              xmlns:sf="http://example.com/sf">
              <gml:featureMember>
                <sf:City>
                  <sf:name>Springfield</sf:name>
                  <sf:location>
                    <gml:Point><gml:pos>-89.6 39.8</gml:pos></gml:Point>
                  </sf:location>
                </sf:City>
              </gml:featureMember>
              <gml:featureMember>
                <sf:City>
                  <sf:name>Shelbyville</sf:name>
                  <sf:location>
                    <gml:Point><gml:pos>-88.5 40.1</gml:pos></gml:Point>
                  </sf:location>
                </sf:City>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """;

        var features = await CollectAsync(gml);

        features.Should().HaveCount(2);
        features[0].Attributes["name"].Should().Be("Springfield");
        features[1].Attributes["name"].Should().Be("Shelbyville");
    }

    // -----------------------------------------------------------------------
    // GML 2 coordinates element
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadStreamingAsync_Gml2Coordinates_ParsesTupleList()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs/1.1"
              xmlns:gml="http://www.opengis.net/gml"
              xmlns:sf="http://example.com">
              <gml:featureMember>
                <sf:Road>
                  <sf:name>Old Road</sf:name>
                  <sf:geom>
                    <gml:LineString srsName="EPSG:4326">
                      <gml:coordinates>-122.1,37.5 -122.2,37.6 -122.3,37.7</gml:coordinates>
                    </gml:LineString>
                  </sf:geom>
                </sf:Road>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """;

        var features = await CollectAsync(gml);

        features.Should().ContainSingle();
        var line = features[0].Geometry.Should().BeOfType<LineString>().Subject;
        line.NumPoints.Should().Be(3);
        line.Coordinates[0].X.Should().BeApproximately(-122.1, 0.0001);
        line.Coordinates[0].Y.Should().BeApproximately(37.5, 0.0001);
    }

    // -----------------------------------------------------------------------
    // SRS detection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryDetectSridAsync_SrsNameOnCollection_ReturnsEpsgCode()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml"
              srsName="urn:ogc:def:crs:EPSG::25832">
            </wfs:FeatureCollection>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gml));
        var srid = await GmlFormatReader.TryDetectSridAsync(stream, CancellationToken.None);

        srid.Should().Be(25832);
    }

    [Fact]
    public async Task TryDetectSridAsync_NoSrsName_ReturnsNull()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml">
            </wfs:FeatureCollection>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gml));
        var srid = await GmlFormatReader.TryDetectSridAsync(stream, CancellationToken.None);

        srid.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Multi-geometry types
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadStreamingAsync_MultiPoint_ReturnsMultiPoint()
    {
        const string gml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml"
              xmlns:sf="http://example.com">
              <gml:featureMember>
                <sf:Cluster>
                  <sf:geom>
                    <gml:MultiPoint>
                      <gml:Point><gml:pos>1 2</gml:pos></gml:Point>
                      <gml:Point><gml:pos>3 4</gml:pos></gml:Point>
                    </gml:MultiPoint>
                  </sf:geom>
                </sf:Cluster>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """;

        var features = await CollectAsync(gml);

        features.Should().ContainSingle();
        var mp = features[0].Geometry.Should().BeOfType<MultiPoint>().Subject;
        mp.NumGeometries.Should().Be(2);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<List<NetTopologySuite.Features.IFeature>> CollectAsync(string gml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gml));
        var results = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in GmlFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            results.Add(feature);
        }

        return results;
    }
}
