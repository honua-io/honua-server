# Legacy OGC CITE Conformance Testing Guide

This guide tracks the manual CITE work needed for the legacy OGC compatibility versions added in ticket #802:

- WMS 1.1.1 Basic
- WFS 1.1.0 Basic
- WFS 1.0.0 Basic

The suites are heavyweight and are not part of PR gates. Run them manually before claiming external conformance, and attach the Team Engine artifacts to the release or PR evidence.

## Supported Endpoints

```text
WMS 1.1.1:
  /rest/services/{serviceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.1.1
  /ogc/services/{serviceId}/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.1.1

WFS 1.1.0:
  /wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.1.0

WFS 1.0.0:
  /wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.0.0
```

## Preflight Checks

```bash
curl -fsS "http://localhost:8080/rest/services/cite/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.1.1" > wms-111-capabilities.xml
curl -fsS "http://localhost:8080/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.1.0" > wfs-110-capabilities.xml
curl -fsS "http://localhost:8080/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.0.0" > wfs-100-capabilities.xml
```

Verify the expected roots before invoking Team Engine:

- WMS 1.1.1: `WMT_MS_Capabilities`
- WFS 1.1.0: `wfs:WFS_Capabilities version="1.1.0"`
- WFS 1.0.0: `WFS_Capabilities version="1.0.0"`

## Team Engine Inputs

Use the official OGC CITE suites for each version:

- WMS 1.1.1: Basic profile with the WMS 1.1.1 capabilities URL.
- WFS 1.1.0: Basic profile with the WFS 1.1.0 capabilities URL.
- WFS 1.0.0: Basic profile with the WFS 1.0.0 capabilities URL.

Record the Team Engine session id, raw XML results, HTML report, capabilities document, and Honua commit SHA.

## Evidence Status

As of ticket #802 implementation, endpoint-level integration tests cover the compatibility surface, but CITE Basic evidence is still pending for all three legacy versions. Do not mark WMS 1.1.1, WFS 1.1.0, or WFS 1.0.0 as CITE certified until the Team Engine artifacts are captured and linked from the relevant release evidence.

## Client Stack Interop

The legacy OGC versions are intended for clients that have not yet adopted the modern WMS 1.3.0 / WFS 2.0 wire formats. Validation of the following client stacks is tracked separately and is not blocked by this server-side surface, but the wire-shape contracts the clients depend on are exercised by the endpoint integration tests:

| Client | Protocol versions consumed | Wire-shape contract verified by |
|---|---|---|
| QGIS (legacy releases) | WMS 1.1.1, WFS 1.1.0 | `<WMT_MS_Capabilities>` + `SRS=` parsing; OWS 1.0 capabilities + GML 3.1.1 features |
| ArcGIS Desktop / ArcMap | WMS 1.1.1, WFS 1.1.0 | EPSG:4326 lon/lat BBOX; `<ServiceExceptionReport>` with DTD reference; OWS 1.0 exceptions |
| GDAL / OGR (`-oo VERSION=...`) | WMS 1.1.1, WFS 1.1.0, WFS 1.0.0 | All three capabilities roots; GML 2.1.2 `<gml:coordinates>`; KVP `MAXFEATURES`/`TYPENAME` |
| Enterprise stacks pinned to WFS 1.0.0 | WFS 1.0.0 | `<WFS_Capabilities version="1.0.0">`; GML 2 `<gml:Box>`/`<gml:coordinates>` text |

Manual client-stack validation (loading a Honua service into each client and confirming layer discovery, render, and feature query) should accompany the CITE Basic runs and be linked from the same release evidence record.
