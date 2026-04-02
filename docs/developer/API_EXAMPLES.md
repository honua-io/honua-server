# Geospatial Data API Examples

This document provides concise, practical examples for Honua Server's geospatial data access protocols. It focuses on data access, not server management.

**Scope**: Minimal request/response examples you can copy to get started. For protocol coverage and parameter support, see the coverage index.

## **Protocol Quick Reference**

| Protocol | Best For | Endpoint | Example Client |
|----------|----------|----------|----------------|
| **FeatureServer REST** | ArcGIS compatibility | `/rest/services/{id}/FeatureServer` | ArcGIS Pro, Esri SDKs |
| **MapServer REST** | Map rendering | `/rest/services/{id}/MapServer` | ArcGIS Pro, Esri SDKs |
| **STAC API** | Catalog discovery and item search | `/stac` | STAC browsers, catalog tooling |
| **OGC API Features** | Standards compliance | `/ogc/features` | QGIS, MapLibre |
| **OData v4** | Business intelligence | `/odata` | Excel, Power BI |
| **Vector Tiles** | High-performance maps | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | MapLibre, Leaflet |

## Table of Contents

- [FeatureServer REST](#geoservices-rest-api)
- [MapServer REST](#mapserver-rest)
- [STAC API](#stac-api)
- [OGC API Features](#ogc-api-features)
- [OData v4](#odata-v4-api)
- [Vector Tiles (MVT)](#vector-tiles-mvt)
- [Error Handling](#error-handling)
- [Related Documentation](#related-documentation)

## Base URL and Auth

All examples assume `http://localhost:8080` as the base URL. If your deployment requires auth, include your configured API key or token.

---

## **GeoServices REST API**

### **Query Features**

```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=json"
```

**Example JSON response (trimmed):**
```json
{
  "features": [
    {
      "attributes": { "OBJECTID": 1, "name": "Downtown", "population": 24500 },
      "geometry": { "x": -122.41, "y": 37.78 }
    }
  ]
}
```

**GeoJSON output:**
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=geojson"
```

**GeoBuf output:**
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=geobuf" --output features.geobuf
```

**FlatGeobuf output:**
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=fgb" --output features.fgb
```

**GeoParquet output:**
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=parquet" --output features.parquet
```

**GeoParquet via Accept header:**
```bash
curl -H "Accept: application/vnd.apache.parquet" \
  "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*" --output features.parquet
```

### **Add Features**

```bash
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/addFeatures" \
  -H "Content-Type: application/json" \
  -d '{"features":[{"geometry":{"x":-122.42,"y":37.77},"attributes":{"name":"New Place"}}]}'
```

---

## **MapServer REST**

### **Export a Map Image**

```bash
curl "http://localhost:8080/rest/services/1/MapServer/export?bbox=-122.5,37.7,-122.3,37.8&size=800,600&format=png&f=image" --output map.png
```

**Export metadata (JSON + base64 image):**
```bash
curl "http://localhost:8080/rest/services/1/MapServer/export?bbox=-122.5,37.7,-122.3,37.8&size=800,600&format=png&f=json"
```

### **Identify Features**

```bash
curl "http://localhost:8080/rest/services/1/MapServer/identify?geometry=-122.41,37.78&geometryType=esriGeometryPoint&sr=4326&mapExtent=-122.5,37.7,-122.3,37.8&imageDisplay=800,600,96&f=json"
```

### **Legend**

```bash
curl "http://localhost:8080/rest/services/1/MapServer/legend?f=json"
```

### **Generate KML / KMZ**

```bash
curl "http://localhost:8080/rest/services/1/MapServer/generateKml?f=kml" --output map.kml
```

```bash
curl "http://localhost:8080/rest/services/1/MapServer/generateKml?f=kmz" --output map.kmz
```

---

## **STAC API**

**Catalog discovery:**

```bash
curl -i "http://localhost:8080/stac"
```

**Example JSON response (trimmed):**
```json
{
  "type": "Catalog",
  "stac_version": "1.0.0",
  "conformsTo": [
    "https://api.stacspec.org/v1.0.0/core",
    "https://api.stacspec.org/v1.0.0/item-search",
    "https://api.stacspec.org/v1.0.0/ogcapi-features",
    "https://api.stacspec.org/v1.0.0/collections",
    "https://api.stacspec.org/v1.0.0/item-search#fields",
    "https://api.stacspec.org/v1.0.0/item-search#sort",
    "https://api.stacspec.org/v1.0.0/item-search#filter"
  ],
  "links": [
    { "rel": "self", "href": "http://localhost:8080/stac" },
    { "rel": "data", "href": "http://localhost:8080/stac/collections" },
    { "rel": "search", "href": "http://localhost:8080/stac/search" }
  ]
}
```

The catalog, collection list, and single-collection metadata routes emit strong `ETag` values and honor `If-None-Match` with `304 Not Modified`.

```bash
etag=$(curl -sI "http://localhost:8080/stac" | awk -F': ' '/^ETag:/ {print $2}' | tr -d '\r')
curl -i -H "If-None-Match: ${etag}" "http://localhost:8080/stac"
```

**Collection detail with declared STAC metadata:**

```bash
curl "http://localhost:8080/stac/collections/0"
```

**Example JSON response (trimmed):**
```json
{
  "type": "Collection",
  "id": "0",
  "license": "CC-BY-4.0",
  "keywords": ["imagery", "ops-demo"],
  "stac_extensions": [
    "https://stac-extensions.github.io/eo/v1.1.0/schema.json",
    "https://stac-extensions.github.io/projection/v1.1.0/schema.json"
  ],
  "links": [
    { "rel": "items", "href": "http://localhost:8080/stac/collections/0/items" },
    { "rel": "alternate", "href": "http://localhost:8080/ogc/features/collections/0" }
  ]
}
```

`license` is always emitted on collections and defaults to `proprietary` when the layer does not declare a STAC-specific license. `keywords` and `stac_extensions` are emitted when declared in the layer's STAC metadata. Collection detail also includes an `alternate` link to the matching OGC API Features collection.

**Collection items:**

```bash
curl "http://localhost:8080/stac/collections/0/items?limit=2&bbox=-158.30,21.20,-157.70,21.70"
```

**Example JSON response (trimmed):**
```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "id": "101",
      "collection": "0",
      "properties": {
        "datetime": "2026-03-30T18:00:00.0000000+00:00",
        "eo:cloud_cover": 17.0,
        "proj:epsg": 4326
      },
      "assets": {
        "geojson": {
          "href": "http://localhost:8080/ogc/features/collections/0/items/101"
        }
      }
    }
  ],
  "links": [
    { "rel": "self", "href": "http://localhost:8080/stac/collections/0/items?limit=2&bbox=-158.30%2C21.20%2C-157.70%2C21.70" },
    { "rel": "next", "href": "http://localhost:8080/stac/collections/0/items?limit=2&offset=2&bbox=-158.30%2C21.20%2C-157.70%2C21.70" }
  ]
}
```

STAC items always include `properties.datetime`. When Honua cannot resolve a time field for the item, that property is still present and set to `null`. Pagination links preserve encoded `bbox` and `datetime` filters so callers can replay the exact query.

**Search via GET:**

```bash
curl "http://localhost:8080/stac/search?collections=0&limit=3&sortby=-observed_at"
```

For manual verification, numeric and RFC 3339 timestamp fields provide the strongest sort evidence. String-only fields may still return `200 OK`, but they are weaker proof that descending order was honored.

**Fields extension probe:**

```bash
curl "http://localhost:8080/stac/search?collections=0&limit=1&fields=properties,-name"
```

Use a removable property such as `name` or `platform` for `fields` probes. Do not try to remove `properties.datetime`; Honua keeps that STAC field in item and search responses and uses `null` when no time value resolves.

**CQL2 text filter probe:**

```bash
curl "http://localhost:8080/stac/search?collections=0&limit=3&filter=quality_score%20%3E%2080&filter-lang=cql2-text"
```

**Search via POST:**

```bash
curl -X POST "http://localhost:8080/stac/search" \
  -H "Content-Type: application/json" \
  -d '{
    "collections": ["0"],
    "limit": 3,
    "sortby": [{ "field": "observed_at", "direction": "desc" }]
  }'
```

---

## **OGC API Features**

Collection IDs are numeric layer IDs (for example, `0`, `1`, `2`).

### **List Collections**

```bash
curl "http://localhost:8080/ogc/features/collections"
```

### **Query Features with BBox**

```bash
curl "http://localhost:8080/ogc/features/collections/0/items?bbox=-122.5,37.7,-122.3,37.8&limit=100"
```

### **Filter with CQL2 (if enabled)**

```bash
curl "http://localhost:8080/ogc/features/collections/0/items?filter=population%20%3E%2010000&filter-lang=cql2-text"
```

### **Output Formats**

**GeoJSON (default for features):**
```bash
curl "http://localhost:8080/ogc/features/collections/0/items?f=geojson"
```

**GML (features only):**
```bash
curl "http://localhost:8080/ogc/features/collections/0/items?f=gml"
```

**HTML (metadata and features):**
```bash
curl "http://localhost:8080/ogc/features/collections?f=html"
```

**Accept header negotiation:**
```bash
curl -H "Accept: application/gml+xml;version=3.2" \
  "http://localhost:8080/ogc/features/collections/0/items"
```

---

## **OData v4 API**

### **Basic Query**

```bash
curl "http://localhost:8080/odata/Features?$select=id,name,population&$filter=population%20gt%2010000&$top=50"
```

### **Count Results**

```bash
curl "http://localhost:8080/odata/Features?$count=true&$top=10"
```

---

## **Vector Tiles (MVT)**

### **MapLibre Example**

```javascript
import maplibregl from 'maplibre-gl';

const map = new maplibregl.Map({
  container: 'map',
  style: {
    version: 8,
    sources: {
      honua: {
        type: 'vector',
        tiles: ['http://localhost:8080/tiles/0/{z}/{x}/{y}.mvt'],
        minzoom: 0,
        maxzoom: 14
      }
    },
    layers: [{
      id: 'layer-fill',
      type: 'fill',
      source: 'honua',
      'source-layer': 'layer',
      paint: { 'fill-color': '#4f46e5', 'fill-opacity': 0.6 }
    }]
  }
});
```

---

## **Error Handling**

**Common HTTP status codes:**
- `400` Bad request or invalid parameters
- `401` Unauthorized (missing/invalid credentials)
- `404` Layer or collection not found
- `500` Server error

**Example error response (shape may vary by protocol):**
```json
{
  "error": {
    "code": 400,
    "message": "Invalid query parameter",
    "details": ["where clause failed to parse"]
  }
}
```

---

## **Related Documentation**

- [Protocols Overview](../gis/STANDARDS_APIS.md)
- [Integration Patterns](INTEGRATION_PATTERNS.md)
- [Admin API Reference](../operator/CONTROL_PLANE_API.md)
- [STAC Ops Demo](../../samples/Honua.StacOpsDemo/README.md)
- [Interactive API Explorer](http://localhost:8080/docs) *(requires running server)* — try endpoints live with Scalar
