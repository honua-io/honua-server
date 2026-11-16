// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Tests.Features.Scene.Bim;

internal static class CityGmlFixtures
{
    public static byte[] SingleBuilding()
    {
        const string xml = """
<?xml version="1.0" encoding="UTF-8"?>
<core:CityModel
    xmlns:core="http://www.opengis.net/citygml/2.0"
    xmlns:bldg="http://www.opengis.net/citygml/building/2.0"
    xmlns:gen="http://www.opengis.net/citygml/generics/2.0"
    xmlns:gml="http://www.opengis.net/gml">
  <gml:boundedBy>
    <gml:Envelope srsName="urn:ogc:def:crs:EPSG::4326" srsDimension="3">
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
        return Encoding.UTF8.GetBytes(xml);
    }

    public static byte[] GmlPosEncoding()
    {
        const string xml = """
<?xml version="1.0" encoding="UTF-8"?>
<core:CityModel
    xmlns:core="http://www.opengis.net/citygml/1.0"
    xmlns:bldg="http://www.opengis.net/citygml/building/1.0"
    xmlns:gml="http://www.opengis.net/gml">
  <core:cityObjectMember>
    <bldg:Building gml:id="BLDG_POS">
      <bldg:boundedBy>
        <bldg:GroundSurface gml:id="GND_POS">
          <bldg:lod1MultiSurface>
            <gml:MultiSurface>
              <gml:surfaceMember>
                <gml:Polygon>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:pos>0.0 0.0 0.0</gml:pos>
                      <gml:pos>0.002 0.0 0.0</gml:pos>
                      <gml:pos>0.002 0.002 0.0</gml:pos>
                      <gml:pos>0.0 0.0 0.0</gml:pos>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:Polygon>
              </gml:surfaceMember>
            </gml:MultiSurface>
          </bldg:lod1MultiSurface>
        </bldg:GroundSurface>
      </bldg:boundedBy>
    </bldg:Building>
  </core:cityObjectMember>
</core:CityModel>
""";
        return Encoding.UTF8.GetBytes(xml);
    }

    public static byte[] BuildingWithoutId()
    {
        const string xml = """
<?xml version="1.0" encoding="UTF-8"?>
<core:CityModel
    xmlns:core="http://www.opengis.net/citygml/2.0"
    xmlns:bldg="http://www.opengis.net/citygml/building/2.0"
    xmlns:gml="http://www.opengis.net/gml">
  <core:cityObjectMember>
    <bldg:Building>
      <bldg:boundedBy>
        <bldg:GroundSurface gml:id="GND_NOID">
          <bldg:lod1MultiSurface>
            <gml:MultiSurface>
              <gml:surfaceMember>
                <gml:Polygon>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList srsDimension="3">0.0 0.0 0.0 0.001 0.0 0.0 0.001 0.001 0.0 0.0 0.0 0.0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:Polygon>
              </gml:surfaceMember>
            </gml:MultiSurface>
          </bldg:lod1MultiSurface>
        </bldg:GroundSurface>
      </bldg:boundedBy>
    </bldg:Building>
  </core:cityObjectMember>
</core:CityModel>
""";
        return Encoding.UTF8.GetBytes(xml);
    }

    public static byte[] NoBuildings()
    {
        const string xml = """
<?xml version="1.0" encoding="UTF-8"?>
<core:CityModel
    xmlns:core="http://www.opengis.net/citygml/2.0"
    xmlns:gml="http://www.opengis.net/gml">
  <gml:boundedBy>
    <gml:Envelope srsName="urn:ogc:def:crs:EPSG::4326"/>
  </gml:boundedBy>
</core:CityModel>
""";
        return Encoding.UTF8.GetBytes(xml);
    }
}
