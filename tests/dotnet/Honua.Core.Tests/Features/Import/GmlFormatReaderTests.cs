// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.Import;

public sealed class GmlFormatReaderTests
{
    private static async Task<List<IFeature>> ReadAllAsync(string gml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gml));
        var features = new List<IFeature>();
        await foreach (var feature in GmlFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }

    [Fact]
    public async Task ReadStreamingAsync_Gml3PosList_ParsesPolygonAndAttributes()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs"
                                   xmlns:gml="http://www.opengis.net/gml"
                                   xmlns:t="http://example.com/t">
              <gml:featureMember>
                <t:zone gml:id="zone.1">
                  <t:name>Central</t:name>
                  <t:population>1200</t:population>
                  <t:the_geom>
                    <gml:Polygon srsName="urn:ogc:def:crs:EPSG::4326">
                      <gml:exterior>
                        <gml:LinearRing>
                          <gml:posList>0 0 0 10 10 10 10 0 0 0</gml:posList>
                        </gml:LinearRing>
                      </gml:exterior>
                    </gml:Polygon>
                  </t:the_geom>
                </t:zone>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """);

        features.Should().ContainSingle();
        var feature = features[0];
        feature.Attributes["gml_id"].Should().Be("zone.1");
        feature.Attributes["name"].Should().Be("Central");
        feature.Attributes["population"].Should().Be("1200");

        var polygon = feature.Geometry.Should().BeOfType<Polygon>().Subject;
        polygon.Shell.Coordinates.Should().HaveCount(5);
        polygon.Shell.IsClosed.Should().BeTrue();
        polygon.Area.Should().Be(100);
    }

    [Fact]
    public async Task ReadStreamingAsync_Gml2Coordinates_ParsesPoint()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <gml:FeatureCollection xmlns:gml="http://www.opengis.net/gml">
              <gml:featureMember>
                <Site>
                  <label>Depot</label>
                  <geom>
                    <gml:Point srsName="EPSG:4326">
                      <gml:coordinates>-122.5,37.8</gml:coordinates>
                    </gml:Point>
                  </geom>
                </Site>
              </gml:featureMember>
            </gml:FeatureCollection>
            """);

        features.Should().ContainSingle();
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        point.X.Should().BeApproximately(-122.5, 1e-9);
        point.Y.Should().BeApproximately(37.8, 1e-9);
        features[0].Attributes["label"].Should().Be("Depot");
    }

    [Fact]
    public async Task ReadStreamingAsync_MultiSurface_ParsesEveryMemberAndPolygon()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs"
                                   xmlns:gml="http://www.opengis.net/gml">
              <gml:featureMember>
                <Tract>
                  <geom>
                    <gml:MultiSurface srsName="EPSG:4326">
                      <gml:surfaceMember>
                        <gml:Polygon>
                          <gml:exterior>
                            <gml:LinearRing>
                              <gml:posList>0 0 0 2 2 2 2 0 0 0</gml:posList>
                            </gml:LinearRing>
                          </gml:exterior>
                        </gml:Polygon>
                      </gml:surfaceMember>
                      <gml:surfaceMember>
                        <gml:Polygon>
                          <gml:exterior>
                            <gml:LinearRing>
                              <gml:posList>5 5 5 6 6 6 6 5 5 5</gml:posList>
                            </gml:LinearRing>
                          </gml:exterior>
                        </gml:Polygon>
                      </gml:surfaceMember>
                    </gml:MultiSurface>
                  </geom>
                </Tract>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """);

        features.Should().ContainSingle();
        var multi = features[0].Geometry.Should().BeOfType<MultiPolygon>().Subject;
        multi.NumGeometries.Should().Be(2);
    }

    [Fact]
    public async Task ReadStreamingAsync_FeatureMembersContainer_YieldsEachFeature()
    {
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs"
                                   xmlns:gml="http://www.opengis.net/gml">
              <gml:featureMembers>
                <City><name>A</name><geom><gml:Point><gml:pos>1 2</gml:pos></gml:Point></geom></City>
                <City><name>B</name><geom><gml:Point><gml:pos>3 4</gml:pos></gml:Point></geom></City>
              </gml:featureMembers>
            </wfs:FeatureCollection>
            """);

        features.Should().HaveCount(2);
        features[0].Attributes["name"].Should().Be("A");
        features[1].Attributes["name"].Should().Be("B");
        ((Point)features[1].Geometry!).X.Should().Be(3);
    }

    [Fact]
    public async Task ReadStreamingAsync_MalformedMember_IsSkippedWithoutAbortingStream()
    {
        // The first member's posList has an odd ordinate count and an unparsable token, but the
        // reader must still surface the second, well-formed feature.
        var features = await ReadAllAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs"
                                   xmlns:gml="http://www.opengis.net/gml">
              <gml:featureMember>
                <Bad><geom><gml:Point><gml:pos>not-a-number</gml:pos></gml:Point></geom></Bad>
              </gml:featureMember>
              <gml:featureMember>
                <Good><name>ok</name><geom><gml:Point><gml:pos>7 8</gml:pos></gml:Point></geom></Good>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """);

        features.Should().Contain(f => Equals(f.Attributes["name"], "ok"));
        var good = features.Single(f => f.Attributes.Exists("name"));
        ((Point)good.Geometry!).Y.Should().Be(8);
    }
}
