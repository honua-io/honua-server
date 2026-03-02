# Geospatial Data APIs (Standards-Based)

Honua exposes multiple industry-standard geospatial APIs. This page helps you choose the right protocol and understand the shape of each API at a high level.

## **Quick Protocol Selection**

| If you're using... | Use this API | Endpoint Pattern | Why |
|-------------------|-------------|------------------|-----|
| **ArcGIS Pro/Desktop** | FeatureServer / MapServer | `/rest/services/{id}/FeatureServer` or `/rest/services/{id}/MapServer` | Esri compatibility (data + maps) |
| **QGIS/OpenLayers** | OGC API Features | `/ogc/features` | Open standards |
| **QGIS/GeoServer clients (legacy OGC)** | WMS 1.3 / WMTS 1.0 | `.../MapServer/WMS` or `.../MapServer/WMTS` | Legacy OGC raster map services |
| **Server-rendered maps (OGC)** | OGC API Maps | `/ogc/maps` | Standards-based rendered map images |
| **Power BI/Excel** | OData v4 | `/odata` | BI integration |
| **Web Maps (MapLibre)** | Vector Tiles + TileJSON | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | Fast rendering with auto-styles |
| **Esri raster/image workflows** | ImageServer | `/rest/services/{id}/ImageServer` | Esri raster compatibility |
| **Esri geometry operations** | Geometry Service | `/rest/services/geometry` | Buffer, simplify, project, intersect, union, clip, difference, area, length |
| **Custom Applications** | Any protocol | Multiple endpoints | Choose by client needs |

---

## **GeoServices REST FeatureServer**

**Best for**: Esri tooling and existing ArcGIS workflows

**Endpoint structure:**
```
/rest/services/{service-name}/FeatureServer/{layer-id}
|-- /query
|-- /addFeatures
|-- /updateFeatures
|-- /deleteFeatures
|-- /applyEdits
```

**Typical use cases:**
- ArcGIS Pro connectivity
- ArcGIS SDK clients
- Legacy FeatureServer integrations

---

## **GeoServices REST MapServer**

**Best for**: Esri map rendering workflows (dynamic map images, identify, legends)

**Endpoint structure:**
```
/rest/services/{service-name}/MapServer
|-- /export
|-- /identify
|-- /legend
|-- /{layer-id}/query
```

**Typical use cases:**
- ArcGIS Pro map rendering
- Dynamic map images for web clients
- Identify and legend requests from Esri tooling

---

## **OGC API Features**

**Best for**: Standards-based, vendor-neutral access

**Endpoint structure:**
```
/ogc/features
|-- /
|-- /conformance
|-- /collections
|-- /collections/{id}
|-- /collections/{id}/items
```

**Output formats:**
- Metadata: `json` or `html`
- Features: `geojson` (default), `json`, `html` (GML output available but not claimed as a conformance class)

**Typical use cases:**
- QGIS and open-source GIS tooling
- Vendor-neutral integration
- Simple feature queries by bbox or filter

---

## **OData v4**

**Best for**: BI tooling and enterprise data integration

**Endpoint structure:**
```
/odata
|-- /
|-- /$metadata
|-- /{entity-set}
|-- /$batch
```

**Typical use cases:**
- Excel and Power BI dashboards
- BI pipelines and reporting
- Non-GIS systems consuming spatial data

---

## **OGC API Maps**

**Best for**: Standards-based server-rendered map images

**Endpoint structure:**
```
/ogc/maps
|-- /conformance
|-- /map
|-- /collections/{id}/map
|-- /collections/{id}/styles/{styleId}/map
|-- /collections/{id}/map/tiles
```

**Output formats:** PNG, JPEG, TIFF

**Typical use cases:**
- Server-rendered maps via open standards
- Dynamic map image generation without Esri dependencies
- OGC-compliant map rendering workflows

---

## **WMS 1.3 / WMTS 1.0**

**Best for**: Legacy OGC map services (QGIS, GeoServer ecosystem clients)

**Endpoint structure:**
```
/rest/services/{id}/MapServer/WMS    (or /ogc/services/{id}/wms)
|-- ?service=WMS&request=GetCapabilities
|-- ?service=WMS&request=GetMap
|-- ?service=WMS&request=GetFeatureInfo

/rest/services/{id}/MapServer/WMTS   (or /ogc/services/{id}/wmts)
|-- ?service=WMTS&request=GetCapabilities
|-- ?service=WMTS&request=GetTile
|-- ?service=WMTS&request=GetFeatureInfo
```

**Limitations:** WMTS currently supports WebMercatorQuad tile matrix set only.

**Typical use cases:**
- QGIS WMS/WMTS layer connections
- Desktop GIS clients expecting legacy OGC services
- INSPIRE/SDI compliance requiring WMS/WMTS endpoints

---

## **GeoServices REST ImageServer**

**Best for**: Esri raster/image workflows

**Endpoint structure:**
```
/rest/services/{id}/ImageServer
|-- /exportImage
|-- /identify
|-- /tile/{level}/{row}/{col}
```

**Typical use cases:**
- ArcGIS Pro raster rendering
- Image export and pixel value queries
- Tiled image serving

---

## **GeoServices REST Geometry Service**

**Best for**: Esri geometry operations

**Endpoint structure:**
```
/rest/services/geometry
|-- /buffer
|-- /simplify
|-- /project
|-- /intersect
|-- /union
|-- /clip
|-- /difference
|-- /area
|-- /length
```

**Typical use cases:**
- Coordinate reprojection
- Geometry buffering and simplification
- Esri SDK geometry helper operations

---

## **Vector Tiles (MVT) + TileJSON**

**Best for**: High-performance web maps

**Endpoint structure:**
```
/tiles/{layerId}/{z}/{x}/{y}.mvt     (vector tiles)
/tiles/{layerId}/tile.json            (TileJSON metadata)
/api/styles/{layerId}.json            (auto-generated MapLibre style)
```

**Typical use cases:**
- MapLibre GL JS maps with auto-generated styles
- Leaflet and Mapbox GL maps
- Fast vector rendering at multiple zoom levels

---

## **Coverage and Compliance**

Protocol support is tracked per standard and operation. Use these docs to confirm supported behaviors:

**GeoServices REST (Esri-compatible):**
- [FeatureServer Coverage Matrix](feature-server-matrix.md) — aligned to [Esri REST Feature Service spec](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/)
- [MapServer Coverage Matrix](map-server-matrix.md) (includes WMS 1.3 and WMTS 1.0) — aligned to [Esri REST Map Service spec](https://developers.arcgis.com/rest/services-reference/enterprise/map-service/)
- [Geometry Service Coverage](specifications/geometry-service-coverage.md) — buffer, simplify, project, intersect, union, clip, difference, area, length

**OGC API:**
- [OGC API Features Coverage](specifications/ogc-api-features-coverage.md)
  - [Part 1 — Core](specifications/ogc-api-features-part1-core.md)
  - [Part 2 — CRS](specifications/ogc-api-features-part2-crs.md)
  - [Part 3 — Filtering](specifications/ogc-api-features-part3-filtering.md)
- [OGC API Tiles Coverage](specifications/ogc-api-tiles-coverage.md)

**OData v4:**
- [OData v4 Coverage](specifications/odata-v4-coverage.md)

**Client validation artifacts:**
- [Client Templates + Manual Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md)
- [Client Template Version Matrix](CLIENT_TEMPLATE_VERSION_MATRIX.md)

**OGC CITE conformance (100% pass rate):**
- OGC API Features: 137/137 tests
- OGC API Tiles: 16/16 tests
- WMS 1.3: 227/227 tests
- WMTS 1.0: 118/118 tests
- OGC API Maps: 32/32 tests

---

## **Related Documentation**

- [MVP Compatibility Contract](MVP_COMPATIBILITY_CONTRACT.md)
- [Geospatial API Examples](API_EXAMPLES.md)
- [Integration Patterns](INTEGRATION_PATTERNS.md)
- [FeatureServer Coverage Matrix](feature-server-matrix.md)
- [MapServer Coverage Matrix](map-server-matrix.md)
