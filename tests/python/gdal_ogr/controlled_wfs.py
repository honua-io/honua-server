# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Authored WFS transport for real-client assertion controls, never Honua qualification."""
import json
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from urllib.parse import parse_qs, urlsplit
from xml.etree import ElementTree as ET
from xml.sax.saxutils import escape

from shapely.geometry import box, shape

GML = "http://www.opengis.net/gml/3.2"


@contextmanager
def controlled_wfs(document):
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            query = {key.upper(): values[0] for key, values in parse_qs(urlsplit(self.path).query).items()}
            operation = query.get("REQUEST", "GetCapabilities")
            requests.append({"operation": operation, "parameters": query})
            url = f"http://127.0.0.1:{self.server.server_port}/wfs"
            if operation.lower() == "getcapabilities":
                operations = "".join(f'<ows:Operation name="{name}"><ows:DCP><ows:HTTP><ows:Get xlink:href="{url}"/></ows:HTTP></ows:DCP></ows:Operation>'
                                     for name in ("GetCapabilities", "DescribeFeatureType", "GetFeature"))
                payload = f'''<wfs:WFS_Capabilities xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:ows="http://www.opengis.net/ows/1.1" xmlns:fes="http://www.opengis.net/fes/2.0" xmlns:gml="{GML}" xmlns:fixture="urn:honua:controlled:wfs" xmlns:xlink="http://www.w3.org/1999/xlink" version="2.0.0">
<ows:ServiceIdentification><ows:Title>Authored assertion control</ows:Title><ows:ServiceType>WFS</ows:ServiceType><ows:ServiceTypeVersion>2.0.0</ows:ServiceTypeVersion></ows:ServiceIdentification>
<ows:OperationsMetadata>{operations}</ows:OperationsMetadata>
<wfs:FeatureTypeList><wfs:FeatureType><wfs:Name>fixture:points</wfs:Name><wfs:Title>Authored points</wfs:Title><wfs:DefaultCRS>urn:ogc:def:crs:OGC:1.3:CRS84</wfs:DefaultCRS><wfs:OutputFormats><wfs:Format>application/gml+xml; version=3.2</wfs:Format></wfs:OutputFormats><ows:WGS84BoundingBox><ows:LowerCorner>-123 37</ows:LowerCorner><ows:UpperCorner>-122 38</ows:UpperCorner></ows:WGS84BoundingBox></wfs:FeatureType></wfs:FeatureTypeList>
<fes:Filter_Capabilities><fes:Scalar_Capabilities><fes:LogicalOperators/><fes:ComparisonOperators><fes:ComparisonOperator name="PropertyIsEqualTo"/></fes:ComparisonOperators></fes:Scalar_Capabilities><fes:Spatial_Capabilities><fes:GeometryOperands><fes:GeometryOperand name="gml:Envelope"/></fes:GeometryOperands><fes:SpatialOperators><fes:SpatialOperator name="BBOX"/></fes:SpatialOperators></fes:Spatial_Capabilities></fes:Filter_Capabilities></wfs:WFS_Capabilities>'''
            elif operation.lower() == "describefeaturetype":
                payload = f'''<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:gml="{GML}" xmlns:fixture="urn:honua:controlled:wfs" targetNamespace="urn:honua:controlled:wfs" elementFormDefault="qualified"><xsd:import namespace="{GML}"/><xsd:element name="points" type="fixture:pointsType" substitutionGroup="gml:AbstractFeature"/><xsd:complexType name="pointsType"><xsd:complexContent><xsd:extension base="gml:AbstractFeatureType"><xsd:sequence><xsd:element name="objectid" type="xsd:long"/><xsd:element name="name" type="xsd:string"/><xsd:element name="geometry" type="gml:PointPropertyType" nillable="true" minOccurs="0"/></xsd:sequence></xsd:extension></xsd:complexContent></xsd:complexType></xsd:schema>'''
            elif operation.lower() == "getfeature":
                features = document["features"]
                if query.get("FILTER"):
                    root = ET.fromstring(query["FILTER"])
                    literals = [element.text for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "Literal"]
                    if literals:
                        features = [feature for feature in features if feature["properties"]["name"] == literals[0]]
                    envelope = next((element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "Envelope"), None)
                    if envelope is not None:
                        lower = [float(value) for value in envelope.find(f"{{{GML}}}lowerCorner").text.split()]
                        upper = [float(value) for value in envelope.find(f"{{{GML}}}upperCorner").text.split()]
                        if envelope.get("srsName", "").startswith("urn:") and envelope.get("srsName", "").endswith("4326"):
                            lower.reverse()
                            upper.reverse()
                        window = box(*lower, *upper)
                        features = [feature for feature in features if feature["geometry"] is not None and shape(feature["geometry"]).intersects(window)]
                if query.get("BBOX"):
                    window = box(*map(float, query["BBOX"].split(",")[:4]))
                    features = [feature for feature in features if feature["geometry"] is not None and shape(feature["geometry"]).intersects(window)]
                total = len(features)
                start = int(query.get("STARTINDEX", 0))
                features = features[start:start + int(query.get("COUNT", 100))]
                if "json" in query.get("OUTPUTFORMAT", ""):
                    payload = json.dumps({"type": "FeatureCollection", "features": features})
                else:
                    members = []
                    if query.get("RESULTTYPE", "").lower() != "hits":
                        for feature in features:
                            geometry = feature["geometry"]
                            point = "" if geometry is None else f'<fixture:geometry><gml:Point srsName="urn:ogc:def:crs:OGC:1.3:CRS84"><gml:pos>{geometry["coordinates"][0]} {geometry["coordinates"][1]}</gml:pos></gml:Point></fixture:geometry>'
                            members.append(f'<wfs:member><fixture:points gml:id="points.{feature["id"]}"><fixture:objectid>{feature["id"]}</fixture:objectid><fixture:name>{escape(feature["properties"]["name"])}</fixture:name>{point}</fixture:points></wfs:member>')
                    payload = f'<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:gml="{GML}" xmlns:fixture="urn:honua:controlled:wfs" numberMatched="{total}" numberReturned="{len(members)}">{"".join(members)}</wfs:FeatureCollection>'
            else:
                self.send_error(400)
                return
            body = payload.encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json" if payload.startswith("{") else "application/xml")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}", requests
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
