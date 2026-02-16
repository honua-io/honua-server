# Geospatial Data APIs (Standards-Based)

Honua exposes multiple industry-standard geospatial APIs. This page helps you choose the right protocol and understand the shape of each API at a high level.

## **Quick Protocol Selection**

| If you're using... | Use this API | Endpoint Pattern | Why |
|-------------------|-------------|------------------|-----|
| **ArcGIS Pro/Desktop** | FeatureServer / MapServer | `/rest/services/{id}/FeatureServer` or `/rest/services/{id}/MapServer` | Esri compatibility (data + maps) |
| **QGIS/OpenLayers** | OGC API Features | `/ogc/features` | Open standards |
| **Power BI/Excel** | OData v4 | `/odata` | BI integration |
| **Web Maps (MapLibre)** | Vector Tiles | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | Fast rendering |
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
- Features: `geojson` (default), `json`, `gml`, `html`

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

## **Vector Tiles (MVT)**

**Best for**: High-performance web maps

**Endpoint structure:**
```
/tiles/{layerId}/{z}/{x}/{y}.mvt
```

**Typical use cases:**
- MapLibre or Leaflet maps
- Fast rendering at multiple zoom levels

---

## **Coverage and Compliance**

Protocol support is tracked per standard and operation. Use these docs to confirm supported behaviors:

**OGC API Features:**
- [OGC API Features Coverage](specifications/ogc-api-features-coverage.md)
- [Part 1 — Core](specifications/ogc-api-features-part1-core.md)
- [Part 2 — CRS](specifications/ogc-api-features-part2-crs.md)
- [Part 3 — Filtering](specifications/ogc-api-features-part3-filtering.md)

**Other protocols:**
- [OData v4 Coverage](specifications/odata-v4-coverage.md)
- [FeatureServer Coverage Matrix](feature-server-matrix.md) — aligned to [Esri REST Feature Service spec](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/)
- [MapServer Coverage Matrix](map-server-matrix.md) — aligned to [Esri REST Map Service spec](https://developers.arcgis.com/rest/services-reference/enterprise/map-service/)

---

## **Related Documentation**

- [Geospatial API Examples](API_EXAMPLES.md)
- [Integration Patterns](INTEGRATION_PATTERNS.md)
- [FeatureServer Coverage Matrix](feature-server-matrix.md)
- [MapServer Coverage Matrix](map-server-matrix.md)
