# Geospatial Data API Examples

This document provides concise, practical examples for Honua Server's geospatial data access protocols. It focuses on data access, not server management.

**Scope**: Minimal request/response examples you can copy to get started. For protocol coverage and parameter support, see the coverage index.

## **Protocol Quick Reference**

| Protocol | Best For | Endpoint | Example Client |
|----------|----------|----------|----------------|
| **FeatureServer REST** | ArcGIS compatibility | `/rest/services/{id}/FeatureServer` | ArcGIS Pro, Esri SDKs |
| **MapServer REST** | Map rendering | `/rest/services/{id}/MapServer` | ArcGIS Pro, Esri SDKs |
| **OGC API Features** | Standards compliance | `/ogc/features` | QGIS, MapLibre |
| **OData v4** | Business intelligence | `/odata` | Excel, Power BI |
| **Vector Tiles** | High-performance maps | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | MapLibre, Leaflet |

## Table of Contents

- [FeatureServer REST](#geoservices-rest-api)
- [MapServer REST](#mapserver-rest)
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

**GeoJSON output:**
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=geojson"
```

**Example response (trimmed):**
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

- [Protocols Overview](STANDARDS_APIS.md)
- [Integration Patterns](INTEGRATION_PATTERNS.md)
- [Admin API Reference](CONTROL_PLANE_API.md)
