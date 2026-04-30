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
| **OGC API Coverages** | Modern raster/coverage access | `/ogc/coverages` | OGC coverage clients, GDAL/QGIS-style workflows |
| **OGC API Processes** | Async geoprocessing | `/ogc/processes` | OGC-compliant process clients |
| **Spec Plan/Apply Engine** | Terraform-style spec execution with content-hash caching | `/v1/spec/*` + `geospatial.v1.SpecService` | Deployment tooling, AI agents |
| **OData v4** | Business intelligence | `/odata` | Excel, Power BI |
| **Vector Tiles** | High-performance maps | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | MapLibre, Leaflet |
| **Terrain-RGB Tiles** | Web terrain/elevation | `/terrain/{datasetId}/tile.json` | MapLibre/Mapbox `raster-dem` clients |

## Table of Contents

- [FeatureServer REST](#geoservices-rest-api)
- [MapServer REST](#mapserver-rest)
- [STAC API](#stac-api)
- [OGC API Features](#ogc-api-features)
- [OGC API Coverages](#ogc-api-coverages)
- [OGC API Processes](#ogc-api-processes)
- [Spec Plan/Apply Engine](#spec-planapply-engine)
- [OData v4](#odata-v4-api)
- [Vector Tiles (MVT)](#vector-tiles-mvt)
- [Terrain-RGB Elevation Tiles](#terrain-rgb-elevation-tiles)
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

**GeoArrow IPC stream output:**
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=arrow" --output features.arrows
```

**GeoArrow via Accept header:**
```bash
curl -H "Accept: application/vnd.apache.arrow.stream" \
  "http://localhost:8080/rest/services/1/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*" --output features.arrows
```

### **Add Features**

```bash
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/addFeatures" \
  -H "Content-Type: application/json" \
  -d '{"features":[{"geometry":{"x":-122.42,"y":37.77},"attributes":{"name":"New Place"}}]}'
```

### **Spatial Analytics: Cluster Features**

FeatureServer analytics routes accept the usual GeoServices-style POST body. The legacy `f=json` flag is tolerated for GeoServices parity, but the response is always GeoJSON (`application/geo+json`) with WGS 84 coordinates and a `metadata` envelope.

```bash
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/queryClusters" \
  -H "Content-Type: application/json" \
  -d '{
    "algorithm": "dbscan",
    "eps": 50000,
    "minPoints": 1,
    "returnHullPerCluster": true,
    "where": "category = '\''test'\''",
    "f": "json"
  }'
```

**Example GeoJSON response (trimmed):**
```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": {
        "type": "Polygon",
        "coordinates": [[[-122.5, 37.7], [-122.3, 37.7], [-122.3, 37.9], [-122.5, 37.9], [-122.5, 37.7]]]
      },
      "properties": {
        "clusterId": 0,
        "featureCount": 2
      }
    }
  ],
  "numberReturned": 1,
  "metadata": {
    "operation": "cluster",
    "inputTruncated": false,
    "resultTruncated": false,
    "maxInputFeatures": 1000,
    "maxOutputRows": 100
  }
}
```

`outStatistics` is only valid when `returnHullPerCluster=true`. Shared filters include `where`, `objectIds`, `geometry`, `geometryType`, `inSR`, `spatialRel`, `time`, and `timeRelation`. Distance-based GeoServices spatial relationships (`esriSpatialRelWithinDistance`, `esriSpatialRelBeyondDistance`) are rejected on the analytics slice because `distance` already has operation-specific meaning on other analytics endpoints. `numberReturned` is always present and equals `features.length` after any truncation. `metadata.maxOutputRows` is populated only when the operation has a distinct result cap: hull-per-cluster responses and density return a number, while per-feature clusters, spatial join, and buffer aggregate return `null`. In per-feature mode, cluster rows keep `properties.objectId` and nested `properties.attributes` from the source feature alongside `clusterId`.

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
When the layer declares STAC item extensions, Honua also emits the declared `stac_extensions` array on item and search hits.

**Search via GET:**

```bash
curl "http://localhost:8080/stac/search?collections=0&limit=3&sortby=-observed_at"
```

For manual verification, numeric and RFC 3339 timestamp fields provide the strongest sort evidence. String-only fields may still return `200 OK`, but they are weaker proof that descending order was honored.

**Fields extension probe:**

```bash
curl "http://localhost:8080/stac/search?collections=0&limit=1&fields=properties,-platform"
```

Use a removable property such as `name` or `platform` for `fields` probes. Do not try to remove `properties.datetime`; Honua keeps that STAC field in item and search responses and uses `null` when no time value resolves.

**CQL2 text filter probe:**

```bash
curl "http://localhost:8080/stac/search?collections=0&limit=5&filter=quality_score%20%3E%3D%2070&filter-lang=cql2-text"
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

### **Spatial Analytics Extensions (Pro)**

The OGC analytics mirrors are POST-only and accept the same request fields as the FeatureServer routes. `application/json` is the canonical content type, `application/x-www-form-urlencoded` is also accepted by the shared POST-body parser, and other POST media types return `415 Unsupported Media Type`.

```bash
curl -X POST "http://localhost:8080/ogc/features/collections/0/density" \
  -H "Content-Type: application/json" \
  -d '{
    "mode": "hex",
    "cellSize": 20000,
    "time": "2023-01-01T00:00:00Z,2023-12-31T23:59:59Z",
    "timeRelation": "esriTimeRelationOverlaps"
  }'
```

**Example GeoJSON response (trimmed):**
```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": {
        "type": "Polygon",
        "coordinates": [[[-122.6, 37.6], [-122.4, 37.6], [-122.3, 37.8], [-122.4, 38.0], [-122.6, 38.0], [-122.7, 37.8], [-122.6, 37.6]]]
      },
      "properties": {
        "cellId": 1,
        "featureCount": 3
      }
    }
  ],
  "numberReturned": 1,
  "metadata": {
    "operation": "density",
    "inputTruncated": false,
    "resultTruncated": false,
    "maxInputFeatures": 1000,
    "maxOutputRows": 1000
  }
}
```

The other OGC analytics mirrors are:
- `POST /ogc/features/collections/{collectionId}/clusters`
- `POST /ogc/features/collections/{collectionId}/spatial-join`
- `POST /ogc/features/collections/{collectionId}/buffer-aggregate`

As with the FeatureServer mirror, `numberReturned` equals `features.length` after truncation, and `metadata.maxOutputRows` is populated for density and cluster hull mode while remaining `null` for per-feature clusters, spatial join, and buffer aggregate. Per-feature cluster and spatial-join rows keep `properties.objectId` plus nested `properties.attributes`; spatial join also exposes `matchCount` and any array-valued `carryFields`, buffer aggregate dissolved rows expose `featureCount`, and density rows expose `cellId`, `featureCount`, and optional `weight`.

---

## **OGC API Coverages**

Collection IDs are numeric raster layer IDs. Only accessible layers enabled for `OGC-API-Coverages` and backed by a primary raster appear in collection discovery.

### **Discover Coverage Collections**

```bash
curl "http://localhost:8080/ogc/coverages/collections"
```

Collection objects include `itemType: "coverage"`, supported output `crs` values, `storageCrs`, `extent.spatial.bbox`, `extent.spatial.storageCrsBbox` when known, grid/domain metadata, default `band_N` fields, and links to the collection schema and coverage bytes.

### **Inspect Selectable Bands**

```bash
curl "http://localhost:8080/ogc/coverages/collections/0/schema"
```

The schema exposes selectable raster bands as `band_1`, `band_2`, and so on. Use those names in the `properties` query parameter.

### **Retrieve a GeoTIFF Clip**

```bash
curl -o coverage.tif \
  "http://localhost:8080/ogc/coverages/collections/0/coverage?bbox=-122.5,37.7,-122.3,37.9"
```

GeoTIFF is the default coverage encoding. The response includes `Content-Bbox` when the raster export reports an extent, and `Content-Crs` as an OGC CRS URI-reference when the output CRS is not WGS 84.

### **Select Bands, Reproject, and Resize**

```bash
curl -o coverage.tif \
  "http://localhost:8080/ogc/coverages/collections/0/coverage?properties=band_3,band_1&crs=EPSG:3857&scale-size=Lon(512),Lat(512)"
```

Use only one of `resolution`, `scale-factor`, or `scale-size` per request. Scaling requests are capped at 8192 pixels on either axis. `resolution` uses native/storage CRS pixel units; use `scale-size` for fixed output dimensions with a different output `crs`.
Coverage response `Link` alternates preserve the current subset, CRS, band, and scaling query parameters while switching `f` between GeoTIFF and PNG.

### **Request PNG by Negotiation**

```bash
curl -H "Accept: image/png" \
  -o coverage.png \
  "http://localhost:8080/ogc/coverages/collections/0/coverage"
```

`f=png` is equivalent. Unsupported coverage options such as `datetime`, `subset`, `scale-axes`, NetCDF, and JPEG return a clear `400` problem response.

---

## **OGC API Processes**

### **Landing Page**

```bash
curl http://localhost:8080/ogc/processes
```

### **List Processes**

```bash
curl http://localhost:8080/ogc/processes/processes
```

### **Describe a Process**

```bash
curl http://localhost:8080/ogc/processes/processes/honua-geoprocessing
```

### **Execute (Async)**

Async execution requires the `Prefer: respond-async` header and only accepts `response: "document"` in V1. The `plan` input must be a JSON object with a `planId` and at least one step. Each step requires a `kind` from the canonical step kinds (`queryFeatures`, `geoprocess`, `aggregate`, `renderMap`, `export`); step input values and `dependsOn` entries must be strings, and `outputs` must be an array of supported artifact-kind strings when present. Geoprocess steps are additionally validated against the built-in process catalog: `processId` must match a catalog entry (e.g. `geometry.buffer`) and required parameters must be supplied. Successful submissions return `201 Created` with `Location` and `Preference-Applied: respond-async` headers.

```bash
curl -X POST http://localhost:8080/ogc/processes/processes/honua-geoprocessing/execution \
  -H "Content-Type: application/json" \
  -H "Prefer: respond-async" \
  -d '{
    "inputs": {
      "plan": {
        "planId": "plan-1",
        "steps": [
          {
            "stepId": "s1",
            "kind": "geoprocess",
            "processId": "geometry.buffer",
            "inputs": {
              "wkb": "AQEAAAAAAAAAAAAAAAAAAAAAAAAA",
              "srid": "4326",
              "distance": "100"
            }
          }
        ]
      }
    }
  }'
```

### **List Jobs**

Returns a `jobList` object with `jobs` array and navigation `links`. Use `?limit=N` to control page size (defaults to `OgcProcesses:DefaultJobLimit`).

```bash
curl "http://localhost:8080/ogc/processes/jobs?limit=10"
```

### **Poll Job Status**

```bash
curl http://localhost:8080/ogc/processes/jobs/{jobId}
```

Succeeded jobs include the OGC `results` relation in the `StatusInfo` document so clients can follow the link to `/jobs/{jobId}/results`.

### **Retrieve Results**

Succeeded jobs return `200 OK` with a document-mode, by-value JSON body keyed by stable output identifiers (OGC API Processes Part 1 §7.11.1). V1's canonical process declares no value-typed outputs, so the body is an empty object (`{}`) until the execution engine populates result storage. Non-terminal jobs return `404` (result not ready), failed jobs return `500`, and dismissed jobs return `410 Gone`.

```bash
curl http://localhost:8080/ogc/processes/jobs/{jobId}/results
```

### **Dismiss a Job**

Cancels a running job. Terminal jobs (successful, failed) return `409 Conflict`; already-dismissed jobs return `200`.

```bash
curl -X DELETE http://localhost:8080/ogc/processes/jobs/{jobId}
```

---

## **Spec Plan/Apply Engine**

Terraform-style plan/apply for canonical spec documents. `plan` is
side-effect-free and reads only catalog/metadata; `apply` streams per-node
events and serves cache hits without re-invoking the compute backend. See the
[Spec Engine reference](SPEC_ENGINE.md) for the full contract, diagnostic
codes, and gRPC surface.

### **Plan a Spec**

```bash
curl -X POST http://localhost:8080/v1/spec/plan \
  -H "Content-Type: application/json" \
  -d '{
    "grammarVersion": "1.0.0",
    "processFamilyVersion": "2026.4",
    "nodes": [
      { "id": "parks", "kind": "compute", "op": "source.layer",
        "parameters": { "layerId": "42" } },
      { "id": "buffered", "kind": "compute", "op": "compute.buffer",
        "inputs": { "source": "@parks" },
        "parameters": { "distanceMeters": "100" } }
    ]
  }'
```

Returns the DAG with per-node `{estimatedRows, estimatedBytes,
estimatedDurationMs}` and structured warnings. Structural errors (cycles,
duplicate ids, unresolved references) return `400` with a stable `code`.

### **Apply a Spec (SSE)**

```bash
curl -N -X POST http://localhost:8080/v1/spec/apply \
  -H "Accept: text/event-stream" \
  -H "Content-Type: application/json" \
  -d @spec.json
```

`Accept: text/event-stream` is required. The apply token is returned on the
`X-Spec-Apply-Token` response header. Each event carries a monotonic
`sequence`, a `kind` (`Queued`, `Running`, `Cached`, `Succeeded`, `Failed`,
`Skipped`, `Warning`, `ApplyStarted`, `ApplyCompleted`, `ApplyCancelled`),
and when applicable an `actualCost` or `summary` payload.

### **Cancel an Apply**

```bash
curl -X POST http://localhost:8080/v1/spec/cancel \
  -H "Content-Type: application/json" \
  -d '{"applyToken":"8d...bd"}'
```

Already-completed nodes remain in the cache. Returns `404 apply-token-unknown`
when the token is not registered (e.g. after a server restart — the apply
registry is in-process for S1).

### **Fetch a Cached Artifact**

```bash
curl -O -J http://localhost:8080/v1/spec/artifact/<sha256>
```

Streams the artifact with its declared content type and sets the
`X-Spec-Content-Hash` response header. Returns `404 artifact-not-found` when
the hash is unknown or has been evicted.

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

## **Terrain-RGB Elevation Tiles**

### **TileJSON metadata**

```bash
curl "http://localhost:8080/terrain/0/tile.json"
```

**Example JSON response (trimmed):**
```json
{
  "tilejson": "3.0.0",
  "scheme": "xyz",
  "tiles": ["http://localhost:8080/terrain/0/{z}/{x}/{y}.png"],
  "minzoom": 0,
  "maxzoom": 22,
  "format": "terrain-rgb",
  "encoding": {
    "type": "mapbox-terrain-rgb",
    "formula": "elevationMeters = -10000 + ((R * 256 * 256 + G * 256 + B) * 0.1)",
    "units": "meters",
    "tileSize": 256
  },
  "source": {
    "datasetId": "0",
    "layerId": 0,
    "rasterCount": 1,
    "sourceCrs": "EPSG:3857",
    "verticalUnitAssumption": "Source values are encoded as meters when no vertical unit is declared."
  },
  "noData": {
    "terrainRgbSentinelMeters": -10000,
    "terrainRgbSentinel": [0, 0, 0]
  },
  "supported": true,
  "unsupportedReasons": []
}
```

### **Download one Terrain-RGB tile**

```bash
curl "http://localhost:8080/terrain/0/0/0/0.png" --output terrain.png
```

Tiles are 256x256 `image/png` responses in WebMercator XYZ coordinates. They use the Mapbox Terrain-RGB formula and encode source no-data or uncovered pixels as opaque RGB `[0,0,0]`, which decodes to `-10000m`.

### **MapLibre source snippet**

```json
{
  "sources": {
    "dem": {
      "type": "raster-dem",
      "url": "http://localhost:8080/terrain/0/tile.json",
      "encoding": "mapbox",
      "tileSize": 256
    }
  },
  "terrain": {
    "source": "dem",
    "exaggeration": 1
  }
}
```

`datasetId` can be a numeric layer id or a layer collection name. Tile requests return `400` for zoom or tile-matrix validation failures, `404` for missing datasets or layers without raster sources, and `422` for unsupported DEM sources such as missing CRS or multi-band rasters.

---

## **Error Handling**

**Common HTTP status codes:**
- `400` Bad request or invalid parameters
- `403` Feature or edition gate blocked the request
- `401` Unauthorized (missing/invalid credentials)
- `404` Layer or collection not found
- `415` Unsupported media type for POST body parsing
- `501` Capability is not available on the active provider
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
