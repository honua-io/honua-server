# API Usage Examples

This document provides comprehensive examples for using Honua Server's multiple API protocols.

## Table of Contents

- [GeoServices REST API](#geoservices-rest-api)
- [OGC API Features](#ogc-api-features)
- [OData v4 API](#odata-v4-api)
- [Vector Tiles (MVT)](#vector-tiles-mvt)
- [Authentication](#authentication)
- [Error Handling](#error-handling)

## GeoServices REST API

The GeoServices REST API provides ArcGIS-compatible endpoints for feature services.

### Service Discovery

```bash
# Get service information
curl "http://localhost:8080/rest/services/1/FeatureServer"

# Get layer information
curl "http://localhost:8080/rest/services/1/FeatureServer/0"
```

### Querying Features

#### Basic Query

```bash
# Get all features (with default limit)
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query" \
  -H "Accept: application/json"
```

#### Spatial Query

```bash
# Point intersection query
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/query" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "geometry={\"x\":-122.4194,\"y\":37.7749}" \
  -d "geometryType=esriGeometryPoint" \
  -d "spatialRel=esriSpatialRelIntersects" \
  -d "f=json"
```

#### Attribute Query

```bash
# WHERE clause query
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query" \
  -G \
  -d "where=name='Test Feature'" \
  -d "f=json"
```

#### Complex Query with Multiple Parameters

```bash
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/query" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "where=category='active' AND created_date > date'2024-01-01'" \
  -d "geometry={\"xmin\":-123,\"ymin\":37,\"xmax\":-122,\"ymax\":38}" \
  -d "geometryType=esriGeometryEnvelope" \
  -d "spatialRel=esriSpatialRelIntersects" \
  -d "outFields=name,category,created_date" \
  -d "orderByFields=name ASC" \
  -d "resultOffset=0" \
  -d "resultRecordCount=100" \
  -d "f=json"
```

### Creating Features

```bash
# Add new features
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/applyEdits" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d 'adds=[{
    "attributes": {
      "name": "New Feature",
      "category": "test"
    },
    "geometry": {
      "x": -122.4194,
      "y": 37.7749
    }
  }]' \
  -d "f=json"
```

### Updating Features

```bash
# Update existing features
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/applyEdits" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d 'updates=[{
    "attributes": {
      "objectid": 123,
      "name": "Updated Feature Name"
    }
  }]' \
  -d "f=json"
```

### Deleting Features

```bash
# Delete features by ID
curl -X POST "http://localhost:8080/rest/services/1/FeatureServer/0/applyEdits" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "deletes=123,124,125" \
  -d "f=json"
```

## OGC API Features

Modern REST/JSON API following OGC standards.

### Service Discovery

```bash
# Landing page
curl "http://localhost:8080/ogc/features" \
  -H "Accept: application/json"

# API conformance
curl "http://localhost:8080/ogc/features/conformance"

# Collections list
curl "http://localhost:8080/ogc/features/collections"

# Collection details
curl "http://localhost:8080/ogc/features/collections/layer1"
```

### Querying Features

#### Basic Query

```bash
# Get features from a collection
curl "http://localhost:8080/ogc/features/collections/layer1/items" \
  -H "Accept: application/geo+json"
```

#### Spatial Query with BBox

```bash
# Bounding box query
curl "http://localhost:8080/ogc/features/collections/layer1/items" \
  -G \
  -d "bbox=-123,37,-122,38" \
  -d "limit=100" \
  -H "Accept: application/geo+json"
```

#### CQL2 Attribute Filter

```bash
# CQL2-Text filter
curl "http://localhost:8080/ogc/features/collections/layer1/items" \
  -G \
  -d "filter=name = 'Test Feature' AND category = 'active'" \
  -d "filter-lang=cql2-text" \
  -H "Accept: application/geo+json"
```

#### CQL2-JSON Filter

```bash
# CQL2-JSON filter (complex logical expressions)
curl -X POST "http://localhost:8080/ogc/features/collections/layer1/items" \
  -H "Content-Type: application/json" \
  -H "Accept: application/geo+json" \
  -d '{
    "filter-lang": "cql2-json",
    "filter": {
      "op": "and",
      "args": [
        {
          "op": "=",
          "args": [
            {"property": "category"},
            "active"
          ]
        },
        {
          "op": ">",
          "args": [
            {"property": "value"},
            100
          ]
        }
      ]
    },
    "limit": 50
  }'
```

### Creating Features

```bash
# Create new feature
curl -X POST "http://localhost:8080/ogc/features/collections/layer1/items" \
  -H "Content-Type: application/geo+json" \
  -d '{
    "type": "Feature",
    "properties": {
      "name": "New Feature",
      "category": "test"
    },
    "geometry": {
      "type": "Point",
      "coordinates": [-122.4194, 37.7749]
    }
  }'
```

### Updating Features

```bash
# Update entire feature (PUT)
curl -X PUT "http://localhost:8080/ogc/features/collections/layer1/items/123" \
  -H "Content-Type: application/geo+json" \
  -d '{
    "type": "Feature",
    "properties": {
      "name": "Updated Feature",
      "category": "active"
    },
    "geometry": {
      "type": "Point",
      "coordinates": [-122.4194, 37.7749]
    }
  }'

# Partial update (PATCH)
curl -X PATCH "http://localhost:8080/ogc/features/collections/layer1/items/123" \
  -H "Content-Type: application/json" \
  -d '{
    "properties": {
      "name": "Partially Updated Name"
    }
  }'
```

### Deleting Features

```bash
# Delete feature by ID
curl -X DELETE "http://localhost:8080/ogc/features/collections/layer1/items/123"
```

## OData v4 API

Provides Excel/Power BI integration with rich querying capabilities.

### Service Discovery

```bash
# Service root
curl "http://localhost:8080/odata/v4"

# Service metadata
curl "http://localhost:8080/odata/v4/$metadata"
```

### Querying Features

#### Basic Query

```bash
# Get all features from a layer
curl "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -H "Accept: application/json"
```

#### Select Specific Properties

```bash
# Select only specific fields
curl "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -G \
  -d '$select=Id,Name,Category' \
  -H "Accept: application/json"
```

#### Filtering

```bash
# Filter with OData syntax
curl "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -G \
  -d '$filter=Name eq '\''Test Feature'\'' and Category eq '\''active'\''' \
  -H "Accept: application/json"

# Numeric comparison
curl "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -G \
  -d '$filter=Value gt 100 and Value lt 500' \
  -H "Accept: application/json"
```

#### Ordering and Paging

```bash
# Order and paginate results
curl "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -G \
  -d '$orderby=Name asc' \
  -d '$top=50' \
  -d '$skip=100' \
  -H "Accept: application/json"
```

#### Spatial Queries

```bash
# Spatial intersection using geo.intersects
curl "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -G \
  -d '$filter=geo.intersects(Geometry, geography'\''POINT(-122.4194 37.7749)'\'')' \
  -H "Accept: application/json"

# Distance query
curl "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -G \
  -d '$filter=geo.distance(Geometry, geography'\''POINT(-122.4194 37.7749)'\'') lt 1000' \
  -H "Accept: application/json"
```

### Creating Features

```bash
# Create new feature
curl -X POST "http://localhost:8080/odata/v4/Layers('layer1')/Features" \
  -H "Content-Type: application/json" \
  -d '{
    "Name": "New OData Feature",
    "Category": "test",
    "Geometry": {
      "type": "Point",
      "coordinates": [-122.4194, 37.7749]
    }
  }'
```

### Updating Features

```bash
# Update feature (PATCH)
curl -X PATCH "http://localhost:8080/odata/v4/Layers('layer1')/Features(123)" \
  -H "Content-Type: application/json" \
  -d '{
    "Name": "Updated via OData",
    "Category": "modified"
  }'
```

### Deleting Features

```bash
# Delete feature
curl -X DELETE "http://localhost:8080/odata/v4/Layers('layer1')/Features(123)"
```

## Vector Tiles (MVT)

Mapbox Vector Tile format for efficient map rendering.

### Getting Tiles

```bash
# Get vector tile
curl "http://localhost:8080/rest/services/1/FeatureServer/0/tiles/{z}/{x}/{y}.pbf" \
  -H "Accept: application/vnd.mapbox-vector-tile"

# Example: Get tile at zoom 10, x=163, y=395
curl "http://localhost:8080/rest/services/1/FeatureServer/0/tiles/10/163/395.pbf" \
  --output tile.pbf
```

### TileJSON Metadata

```bash
# Get TileJSON metadata for the layer
curl "http://localhost:8080/rest/services/1/FeatureServer/0/tiles/metadata" \
  -H "Accept: application/json"
```

## Authentication

Admin endpoints require API key authentication using the `HONUA_ADMIN_PASSWORD` environment variable.

```bash
# Set admin password
export HONUA_ADMIN_PASSWORD="your-secure-password"

# Use password as API key for admin endpoints
curl "http://localhost:8080/api/admin/connections/test/tables" \
  -H "X-API-Key: your-secure-password"
```

### Admin Endpoints

```bash
# Test database connection
curl "http://localhost:8080/api/admin/connections/test" \
  -H "X-API-Key: your-secure-password"

# List available tables
curl "http://localhost:8080/api/admin/connections/test/tables" \
  -H "X-API-Key: your-secure-password"

# Get table schema
curl "http://localhost:8080/api/admin/connections/test/tables/your_table/schema" \
  -H "X-API-Key: your-secure-password"
```

## Error Handling

### Common HTTP Status Codes

- **200 OK**: Successful request
- **201 Created**: Feature created successfully
- **204 No Content**: Feature updated/deleted successfully
- **400 Bad Request**: Invalid request parameters or syntax
- **401 Unauthorized**: Missing or invalid API key (admin endpoints)
- **404 Not Found**: Resource not found
- **500 Internal Server Error**: Server error

### Example Error Responses

#### GeoServices REST Error

```json
{
  "error": {
    "code": 400,
    "message": "Invalid query syntax: WHERE clause format not supported",
    "details": ["Use simple comparisons like: name = 'value' or age > 18"]
  }
}
```

#### OGC API Features Error

```json
{
  "code": "InvalidParameterValue",
  "description": "Bbox parameter must contain exactly 4 or 6 comma-separated values"
}
```

#### OData v4 Error

```json
{
  "error": {
    "code": "BadRequest",
    "message": "Invalid field name in $orderby: invalid_field",
    "target": "$orderby"
  }
}
```

## Performance Tips

1. **Use appropriate limits**: Don't request more features than needed
   ```bash
   # Good: Limited result set
   curl "http://localhost:8080/ogc/features/collections/layer1/items?limit=100"
   ```

2. **Select only needed fields**: Reduce bandwidth by selecting specific properties
   ```bash
   # OData: Select specific fields
   curl "http://localhost:8080/odata/v4/Layers('layer1')/Features?\$select=Id,Name"
   ```

3. **Use spatial filters**: Limit queries to areas of interest
   ```bash
   # Bounding box filter
   curl "http://localhost:8080/ogc/features/collections/layer1/items?bbox=-123,37,-122,38"
   ```

4. **Leverage caching**: Use appropriate cache headers for tiles and metadata
   ```bash
   curl "http://localhost:8080/rest/services/1/FeatureServer/0/tiles/10/163/395.pbf" \
     -H "Cache-Control: max-age=3600"
   ```

## Integration Examples

### JavaScript (Fetch API)

```javascript
// Query features using OGC API
async function getFeatures(layerName, bbox = null) {
  const url = new URL(`http://localhost:8080/ogc/features/collections/${layerName}/items`);

  if (bbox) {
    url.searchParams.append('bbox', bbox.join(','));
  }

  const response = await fetch(url, {
    headers: {
      'Accept': 'application/geo+json'
    }
  });

  if (!response.ok) {
    throw new Error(`HTTP ${response.status}: ${response.statusText}`);
  }

  return response.json();
}

// Create a new feature
async function createFeature(layerName, feature) {
  const response = await fetch(`http://localhost:8080/ogc/features/collections/${layerName}/items`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/geo+json'
    },
    body: JSON.stringify(feature)
  });

  if (!response.ok) {
    throw new Error(`HTTP ${response.status}: ${response.statusText}`);
  }

  return response.json();
}
```

### Python (requests)

```python
import requests
import json

# Query features with CQL2 filter
def query_features_cql2(base_url, collection_id, cql_filter):
    url = f"{base_url}/ogc/features/collections/{collection_id}/items"

    params = {
        'filter': cql_filter,
        'filter-lang': 'cql2-text',
        'limit': 100
    }

    headers = {
        'Accept': 'application/geo+json'
    }

    response = requests.get(url, params=params, headers=headers)
    response.raise_for_status()

    return response.json()

# Example usage
features = query_features_cql2(
    'http://localhost:8080',
    'layer1',
    "name LIKE 'Test%' AND category = 'active'"
)
```

### Power BI / Excel (OData)

1. In Power BI, select "Get Data" → "OData Feed"
2. Enter URL: `http://localhost:8080/odata/v4`
3. Navigate to `Layers('your_layer_name')/Features`
4. Use Power BI's query editor to apply additional filters

For Excel:
1. Data → Get Data → From Other Sources → From OData Feed
2. Enter URL: `http://localhost:8080/odata/v4`
3. Select the desired layer and features
4. Load data into Excel worksheet

This comprehensive API documentation should help users effectively integrate with all of Honua Server's supported protocols.