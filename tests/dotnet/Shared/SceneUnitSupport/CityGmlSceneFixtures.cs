// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Inline CityGML documents for the wired ingest tests (#1207). Kept in source
/// so the exact XML driving the ingest executor and admin endpoint is visible
/// alongside the assertions.
/// </summary>
internal static class CityGmlSceneFixtures
{
    /// <summary>
    /// A minimal CityGML 2.0 city model with one building (ground + roof + two
    /// walls) in geographic EPSG:4326 lon/lat degrees, carrying a building-level
    /// generic attribute and storey count. The small lon/lat coordinates place
    /// the building on the ellipsoid through the geographic pass-through.
    /// </summary>
    public static byte[] SingleBuildingGeographic()
        => Encoding.UTF8.GetBytes(BuildXml("urn:ogc:def:crs:EPSG::4326"));

    /// <summary>
    /// The same building declared in a projected CRS (EPSG:25832, UTM 32N) to
    /// exercise the v1 unsupported-CRS rejection path.
    /// </summary>
    public static byte[] SingleBuildingProjected()
        => Encoding.UTF8.GetBytes(BuildXml("urn:ogc:def:crs:EPSG::25832"));

    private static string BuildXml(string srsName) => $$"""
<?xml version="1.0" encoding="UTF-8"?>
<core:CityModel
    xmlns:core="http://www.opengis.net/citygml/2.0"
    xmlns:bldg="http://www.opengis.net/citygml/building/2.0"
    xmlns:gen="http://www.opengis.net/citygml/generics/2.0"
    xmlns:gml="http://www.opengis.net/gml">
  <gml:boundedBy>
    <gml:Envelope srsName="{{srsName}}" srsDimension="3">
      <gml:lowerCorner>0.0 0.0 0.0</gml:lowerCorner>
      <gml:upperCorner>0.001 0.001 10.0</gml:upperCorner>
    </gml:Envelope>
  </gml:boundedBy>
  <core:cityObjectMember>
    <bldg:Building gml:id="BLDG_1">
      <gml:name>Test Tower</gml:name>
      <bldg:storeysAboveGround>3</bldg:storeysAboveGround>
      <gen:stringAttribute name="usage">office</gen:stringAttribute>
      <bldg:boundedBy>
        <bldg:GroundSurface gml:id="GND_1">
          <bldg:lod2MultiSurface>
            <gml:MultiSurface>
              <gml:surfaceMember>
                <gml:Polygon>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList srsDimension="3">0.0 0.0 0.0 0.001 0.0 0.0 0.001 0.001 0.0 0.0 0.001 0.0 0.0 0.0 0.0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:Polygon>
              </gml:surfaceMember>
            </gml:MultiSurface>
          </bldg:lod2MultiSurface>
        </bldg:GroundSurface>
      </bldg:boundedBy>
      <bldg:boundedBy>
        <bldg:RoofSurface gml:id="ROOF_1">
          <gen:stringAttribute name="material">concrete</gen:stringAttribute>
          <bldg:lod2MultiSurface>
            <gml:MultiSurface>
              <gml:surfaceMember>
                <gml:Polygon>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList srsDimension="3">0.0 0.0 10.0 0.001 0.0 10.0 0.001 0.001 10.0 0.0 0.001 10.0 0.0 0.0 10.0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:Polygon>
              </gml:surfaceMember>
            </gml:MultiSurface>
          </bldg:lod2MultiSurface>
        </bldg:RoofSurface>
      </bldg:boundedBy>
      <bldg:boundedBy>
        <bldg:WallSurface gml:id="WALL_1">
          <bldg:lod2MultiSurface>
            <gml:MultiSurface>
              <gml:surfaceMember>
                <gml:Polygon>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList srsDimension="3">0.0 0.0 0.0 0.001 0.0 0.0 0.001 0.0 10.0 0.0 0.0 10.0 0.0 0.0 0.0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:Polygon>
              </gml:surfaceMember>
            </gml:MultiSurface>
          </bldg:lod2MultiSurface>
        </bldg:WallSurface>
      </bldg:boundedBy>
      <bldg:boundedBy>
        <bldg:WallSurface gml:id="WALL_2">
          <bldg:lod2MultiSurface>
            <gml:MultiSurface>
              <gml:surfaceMember>
                <gml:Polygon>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList srsDimension="3">0.001 0.0 0.0 0.001 0.001 0.0 0.001 0.001 10.0 0.001 0.0 10.0 0.001 0.0 0.0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:Polygon>
              </gml:surfaceMember>
            </gml:MultiSurface>
          </bldg:lod2MultiSurface>
        </bldg:WallSurface>
      </bldg:boundedBy>
    </bldg:Building>
  </core:cityObjectMember>
</core:CityModel>
""";
}
