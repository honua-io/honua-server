# Geospatial Data APIs (Standards-Based)

Honua exposes multiple industry-standard geospatial APIs. This page helps you choose the right protocol and understand the shape of each API at a high level.

**Scope**: Protocol selection and quick orientation. For exact capabilities and supported operations, use the coverage index and the API examples.

## **Quick Protocol Selection**

| If you're using... | Use this API | Endpoint Pattern | Why |
|-------------------|-------------|------------------|-----|
| **ArcGIS Pro/Desktop** | FeatureServer | `/rest/services/{id}/FeatureServer` | Esri compatibility |
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
- [Protocol Coverage Index](specifications/protocol-coverage.md)
- [OGC API Features Part 1 (Core)](specifications/ogc-api-features-part1-core.md)
- [OGC API Features Part 2 (CRS)](specifications/ogc-api-features-part2-crs.md)
- [OGC API Features Part 3 (Filtering)](specifications/ogc-api-features-part3-filtering.md)
- [OData v4 Coverage](specifications/odata-v4-coverage.md)

---

## **Related Documentation**

- [Geospatial API Examples](API_EXAMPLES.md)
- [Integration Patterns](INTEGRATION_PATTERNS.md)
- [FeatureServer Coverage Matrix](feature-server-matrix.md)
