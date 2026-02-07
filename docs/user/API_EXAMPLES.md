# Geospatial Data API Examples

This document provides comprehensive examples for using Honua Server's geospatial data access protocols (not the server management API).

## 🌍 **Protocol Quick Reference**

| Protocol | Best For | Endpoint | Example Client |
|----------|----------|----------|----------------|
| **FeatureServer REST** | ArcGIS compatibility | `/rest/services/{id}/FeatureServer` | ArcGIS Pro, Esri SDKs |
| **OGC API Features** | Standards compliance | `/ogc/features` | QGIS, MapLibre |
| **OData v4** | Business intelligence | `/odata` | Excel, Power BI |
| **Vector Tiles** | High-performance maps | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | MapLibre, Leaflet |

*📸 Placeholder: Interactive protocol selection flowchart*

## Table of Contents

- [Multi-Language SDK Examples](#multi-language-sdk-examples)
- [GeoServices REST API](#geoservices-rest-api)
- [OGC API Features](#ogc-api-features)
- [OData v4 API](#odata-v4-api)
- [Vector Tiles (MVT)](#vector-tiles-mvt)
- [Integration Patterns](#integration-patterns)
- [Authentication](#authentication)
- [Error Handling](#error-handling)

## 💻 **Multi-Language SDK Examples**

### **JavaScript/TypeScript**

**ArcGIS REST JS (FeatureServer)**
```javascript
import { queryFeatures, addFeatures } from '@esri/arcgis-rest-feature-service';

// Query features with spatial filter
const queryResponse = await queryFeatures({
  url: 'http://localhost:8080/rest/services/1/FeatureServer/0',
  where: "population > 10000",
  geometry: {
    xmin: -122.5,
    ymin: 37.7,
    xmax: -122.3,
    ymax: 37.8
  },
  geometryType: 'esriGeometryEnvelope',
  spatialRel: 'esriSpatialRelIntersects'
});

console.log(`Found ${queryResponse.features.length} features`);
```

**OGC API Features Client**
```javascript
// Modern fetch-based client
class OGCFeaturesClient {
  constructor(baseUrl) {
    this.baseUrl = baseUrl;
  }

  async getCollections() {
    const response = await fetch(`${this.baseUrl}/ogc/features/collections`);
    return response.json();
  }

  async getFeatures(collectionId, options = {}) {
    const params = new URLSearchParams();
    if (options.bbox) params.append('bbox', options.bbox.join(','));
    if (options.limit) params.append('limit', options.limit);
    if (options.filter) params.append('filter', options.filter);

    const response = await fetch(
      `${this.baseUrl}/ogc/features/collections/${collectionId}/items?${params}`
    );
    return response.json();
  }

  async createFeature(collectionId, geoJsonFeature) {
    const response = await fetch(
      `${this.baseUrl}/ogc/features/collections/${collectionId}/items`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/geo+json' },
        body: JSON.stringify(geoJsonFeature)
      }
    );
    return response.json();
  }
}

// Usage example
const client = new OGCFeaturesClient('http://localhost:8080');

// Get all features in San Francisco bay area
const features = await client.getFeatures('layer1', {
  bbox: [-122.5, 37.7, -122.3, 37.8],
  limit: 100
});
```

**Vector Tiles with MapLibre**
```javascript
import maplibregl from 'maplibre-gl';

// High-performance vector tile map
const map = new maplibregl.Map({
  container: 'map',
  style: {
    version: 8,
    sources: {
      'honua-tiles': {
        type: 'vector',
        tiles: ['http://localhost:8080/tiles/{layerId}/{z}/{x}/{y}.mvt'],
        minzoom: 0,
        maxzoom: 14
      }
    },
    layers: [{
      id: 'features-fill',
      type: 'fill',
      source: 'honua-tiles',
      'source-layer': 'features',
      paint: {
        'fill-color': [
          'case',
          ['<', ['get', 'population'], 1000], '#ffffcc',
          ['<', ['get', 'population'], 5000], '#a1dab4',
          ['<', ['get', 'population'], 10000], '#41b6c4',
          '#225ea8'
        ],
        'fill-opacity': 0.8
      }
    }]
  }
});

// Add interactivity
map.on('click', 'features-fill', (e) => {
  new maplibregl.Popup()
    .setLngLat(e.lngLat)
    .setHTML(
      `<h3>${e.features[0].properties.name}</h3>
       <p>Population: ${e.features[0].properties.population?.toLocaleString()}</p>`
    )
    .addTo(map);
});
```

### **Python**

**GeoServices with Requests**
```python
import requests
import json

class HonuaGeoServicesClient:
    def __init__(self, base_url):
        self.base_url = base_url
        self.session = requests.Session()

    def query_features(self, service_id, layer_id, **params):
        """Query features with flexible parameters"""
        url = f"{self.base_url}/rest/services/{service_id}/FeatureServer/{layer_id}/query"

        # Default parameters
        query_params = {
            'f': 'json',
            'where': '1=1',
            'returnGeometry': 'true',
            'outFields': '*'
        }

        # Override with user parameters
        query_params.update(params)

        response = self.session.get(url, params=query_params)
        response.raise_for_status()
        return response.json()

    def apply_edits(self, service_id, layer_id, adds=None, updates=None, deletes=None):
        """Apply feature edits (create, update, delete)"""
        url = f"{self.base_url}/rest/services/{service_id}/FeatureServer/{layer_id}/applyEdits"

        data = {
            'f': 'json',
            'rollbackOnFailure': 'true'
        }

        if adds:
            data['adds'] = json.dumps(adds)
        if updates:
            data['updates'] = json.dumps(updates)
        if deletes:
            data['deletes'] = ','.join(map(str, deletes))

        response = self.session.post(url, data=data)
        response.raise_for_status()
        return response.json()

# Usage example
client = HonuaGeoServicesClient('http://localhost:8080')

# Spatial query with population filter
features = client.query_features(
    service_id=1,
    layer_id=0,
    where="population > 5000",
    geometry=json.dumps({
        "xmin": -122.5,
        "ymin": 37.7,
        "xmax": -122.3,
        "ymax": 37.8
    }),
    geometryType="esriGeometryEnvelope",
    spatialRel="esriSpatialRelIntersects"
)

print(f"Found {len(features['features'])} features")
```

**OGC API with GeoPandas**
```python
import geopandas as gpd
import requests
from shapely.geometry import box

class HonuaOGCClient:
    def __init__(self, base_url):
        self.base_url = base_url

    def get_collections(self):
        """Get available feature collections"""
        response = requests.get(f"{self.base_url}/ogc/features/collections")
        response.raise_for_status()
        return response.json()

    def get_features_as_geodataframe(self, collection_id, bbox=None, limit=None, cql_filter=None):
        """Get features as a GeoPandas GeoDataFrame"""
        params = {'f': 'json'}

        if bbox:
            params['bbox'] = ','.join(map(str, bbox))
        if limit:
            params['limit'] = limit
        if cql_filter:
            params['filter'] = cql_filter
            params['filter-lang'] = 'cql2-text'

        response = requests.get(
            f"{self.base_url}/ogc/features/collections/{collection_id}/items",
            params=params
        )
        response.raise_for_status()

        geojson_data = response.json()
        return gpd.GeoDataFrame.from_features(geojson_data['features'])

    def create_feature(self, collection_id, geodataframe_row):
        """Create a new feature from a GeoDataFrame row"""
        feature = {
            "type": "Feature",
            "geometry": geodataframe_row.geometry.__geo_interface__,
            "properties": geodataframe_row.drop('geometry').to_dict()
        }

        response = requests.post(
            f"{self.base_url}/ogc/features/collections/{collection_id}/items",
            json=feature,
            headers={'Content-Type': 'application/geo+json'}
        )
        response.raise_for_status()
        return response.json()

# Usage example
client = HonuaOGCClient('http://localhost:8080')

# Get features in San Francisco as GeoDataFrame
sf_bbox = [-122.5, 37.7, -122.3, 37.8]
gdf = client.get_features_as_geodataframe(
    'layer1',
    bbox=sf_bbox,
    cql_filter="population > 10000"
)

# Perform spatial analysis
gdf['area'] = gdf.geometry.area
high_density = gdf[gdf['population'] / gdf['area'] > 1000]

print(f"Found {len(high_density)} high-density areas")
```

**OData with Pandas**
```python
import pandas as pd
import requests

class HonuaODataClient:
    def __init__(self, base_url):
        self.base_url = base_url

    def query_to_dataframe(self, entity_set, filter_expr=None, select=None, orderby=None, top=None):
        """Query OData endpoint and return as pandas DataFrame"""
        params = {}

        if filter_expr:
            params['$filter'] = filter_expr
        if select:
            params['$select'] = ','.join(select) if isinstance(select, list) else select
        if orderby:
            params['$orderby'] = orderby
        if top:
            params['$top'] = top

        response = requests.get(
            f"{self.base_url}/odata/{entity_set}",
            params=params
        )
        response.raise_for_status()

        data = response.json()
        return pd.DataFrame(data['value'])

    def spatial_query(self, filter_expr, distance_km=None):
        """Perform spatial queries with distance"""
        if distance_km:
            filter_expr += f" and geo.distance(geometry, geography'POINT(-122.4 37.8)') lt {distance_km * 1000}"

        return self.query_to_dataframe('Features', filter_expr)

# Usage example
client = HonuaODataClient('http://localhost:8080')

# Get features with spatial and attribute filters
features_df = client.query_to_dataframe(
    'Features',
    filter_expr="LayerId eq 1 and population gt 5000",
    select=['name', 'population', 'geometry'],
    orderby='population desc',
    top=100
)

# Spatial distance query
nearby_features = client.spatial_query(
    "LayerId eq 1 and category eq 'restaurant'",
    distance_km=1
)

print(f"Found {len(nearby_features)} restaurants within 1km")
```

### **C# / .NET**

**GeoServices with HttpClient**
```csharp
using System.Text.Json;
using System.Text;

public class HonuaGeoServicesClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public HonuaGeoServicesClient(string baseUrl)
    {
        _baseUrl = baseUrl;
        _httpClient = new HttpClient();
    }

    public async Task<QueryResponse> QueryFeaturesAsync(
        int serviceId,
        int layerId,
        string whereClause = "1=1",
        object geometry = null,
        string spatialRel = "esriSpatialRelIntersects")
    {
        var url = $"{_baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/query";

        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = whereClause,
            ["returnGeometry"] = "true",
            ["outFields"] = "*"
        };

        if (geometry != null)
        {
            parameters["geometry"] = JsonSerializer.Serialize(geometry);
            parameters["spatialRel"] = spatialRel;
        }

        var content = new FormUrlEncodedContent(parameters);
        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<QueryResponse>(json);
    }

    public async Task<ApplyEditsResponse> ApplyEditsAsync(
        int serviceId,
        int layerId,
        IEnumerable<Feature> adds = null,
        IEnumerable<Feature> updates = null,
        IEnumerable<int> deletes = null)
    {
        var url = $"{_baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits";

        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["rollbackOnFailure"] = "true"
        };

        if (adds?.Any() == true)
            parameters["adds"] = JsonSerializer.Serialize(adds);

        if (updates?.Any() == true)
            parameters["updates"] = JsonSerializer.Serialize(updates);

        if (deletes?.Any() == true)
            parameters["deletes"] = string.Join(",", deletes);

        var content = new FormUrlEncodedContent(parameters);
        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApplyEditsResponse>(json);
    }
}

// Usage example
var client = new HonuaGeoServicesClient("http://localhost:8080");

// Query with spatial filter
var bbox = new
{
    xmin = -122.5,
    ymin = 37.7,
    xmax = -122.3,
    ymax = 37.8
};

var features = await client.QueryFeaturesAsync(
    serviceId: 1,
    layerId: 0,
    whereClause: "population > 5000",
    geometry: bbox
);

Console.WriteLine($"Found {features.Features.Length} features");
```

**OData with OData Client**
```csharp
using Microsoft.OData.Client;

public class HonuaDataContext : DataServiceContext
{
    public HonuaDataContext(Uri serviceRoot) : base(serviceRoot) { }

    public DataServiceQuery<Feature> Features => CreateQuery<Feature>("Features");
    public DataServiceQuery<Layer> Layers => CreateQuery<Layer>("Layers");
}

// Usage with LINQ
var context = new HonuaDataContext(new Uri("http://localhost:8080/odata"));

// Query with LINQ
var urbanAreas = await context.Features
    .Where(f => f.LayerId == 1 && f.Population > 10000)
    .OrderByDescending(f => f.Population)
    .Take(50)
    .ExecuteAsync();

// Spatial query with geo functions (if supported)
var nearbyFeatures = await context.Features
    .AddQueryOption("$filter", "geo.distance(geometry, geography'POINT(-122.4 37.8)') lt 1000")
    .ExecuteAsync();

Console.WriteLine($"Found {nearbyFeatures.Count()} features within 1km");
```

### **R**

**Spatial Data Analysis with R**
```r
library(httr)
library(jsonlite)
library(sf)
library(dplyr)

# Function to query Honua OGC API
query_honua_features <- function(base_url, collection_id, bbox = NULL, filter = NULL, limit = NULL) {
  url <- paste0(base_url, "/ogc/features/collections/", collection_id, "/items")

  # Build query parameters
  params <- list(f = "json")
  if (!is.null(bbox)) params$bbox <- paste(bbox, collapse = ",")
  if (!is.null(filter)) {
    params$filter <- filter
    params$`filter-lang` <- "cql2-text"
  }
  if (!is.null(limit)) params$limit <- limit

  # Make request
  response <- GET(url, query = params)
  stop_for_status(response)

  # Parse GeoJSON and convert to sf
  geojson_data <- content(response, "text") %>% fromJSON()

  # Convert to sf object
  if (length(geojson_data$features) > 0) {
    return(st_read(content(response, "text"), quiet = TRUE))
  } else {
    return(st_sf())
  }
}

# Usage example
base_url <- "http://localhost:8080"

# Get features in San Francisco Bay Area
sf_bbox <- c(-122.5, 37.7, -122.3, 37.8)
features <- query_honua_features(
  base_url,
  "layer1",
  bbox = sf_bbox,
  filter = "population > 5000",
  limit = 1000
)

# Spatial analysis with sf
features <- features %>%
  mutate(
    area_km2 = as.numeric(st_area(geometry)) / 1000000,
    density = population / area_km2
  )

# Find high density areas
high_density <- features %>%
  filter(density > 1000) %>%
  arrange(desc(density))

cat("Found", nrow(high_density), "high-density areas\n")

# Plot with ggplot2
library(ggplot2)

ggplot(features) +
  geom_sf(aes(fill = density)) +
  scale_fill_viridis_c(name = "Population\nDensity") +
  theme_minimal() +
  labs(title = "Population Density Map")
```

### **Java**

**GeoServices with Spring WebClient**
```java
import org.springframework.web.reactive.function.client.WebClient;
import reactor.core.publisher.Mono;

@Service
public class HonuaGeoServicesClient {

    private final WebClient webClient;

    public HonuaGeoServicesClient(String baseUrl) {
        this.webClient = WebClient.builder()
            .baseUrl(baseUrl)
            .build();
    }

    public Mono<QueryResponse> queryFeatures(int serviceId, int layerId, QueryParameters params) {
        return webClient.get()
            .uri(uriBuilder -> uriBuilder
                .path("/rest/services/{serviceId}/FeatureServer/{layerId}/query")
                .queryParam("f", "json")
                .queryParam("where", params.getWhereClause())
                .queryParam("returnGeometry", "true")
                .queryParam("outFields", "*")
                .build(serviceId, layerId))
            .retrieve()
            .bodyToMono(QueryResponse.class);
    }

    public Mono<ApplyEditsResponse> applyEdits(int serviceId, int layerId, ApplyEditsRequest request) {
        MultiValueMap<String, String> formData = new LinkedMultiValueMap<>();
        formData.add("f", "json");
        formData.add("rollbackOnFailure", "true");

        if (request.getAdds() != null && !request.getAdds().isEmpty()) {
            formData.add("adds", objectMapper.writeValueAsString(request.getAdds()));
        }

        return webClient.post()
            .uri("/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits", serviceId, layerId)
            .contentType(MediaType.APPLICATION_FORM_URLENCODED)
            .body(BodyInserters.fromFormData(formData))
            .retrieve()
            .bodyToMono(ApplyEditsResponse.class);
    }
}

// Usage example
@Autowired
private HonuaGeoServicesClient honuaClient;

public void demonstrateUsage() {
    // Query features
    QueryParameters params = QueryParameters.builder()
        .whereClause("population > 5000")
        .spatialFilter(BoundingBox.of(-122.5, 37.7, -122.3, 37.8))
        .build();

    QueryResponse response = honuaClient.queryFeatures(1, 0, params)
        .block(); // In real code, use reactive chains

    System.out.println("Found " + response.getFeatures().size() + " features");

    // Create new features
    List<Feature> newFeatures = Arrays.asList(
        Feature.builder()
            .geometry(Point.of(-122.4, 37.8))
            .attributes(Map.of("name", "New Point", "population", 1500))
            .build()
    );

    ApplyEditsRequest editRequest = ApplyEditsRequest.builder()
        .adds(newFeatures)
        .build();

    ApplyEditsResponse editResponse = honuaClient.applyEdits(1, 0, editRequest)
        .block();

    System.out.println("Created " + editResponse.getAddResults().size() + " features");
}
```

*📸 Placeholder: Code editor showing multi-language SDK examples*

---

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
curl "http://localhost:8080/tiles/0/tile.json" \
  -H "Accept: application/json"

# Get MapLibre style JSON for the layer
curl "http://localhost:8080/api/styles/0.json" \
  -H "Accept: application/json"
```

## Authentication

Admin endpoints accept OIDC bearer tokens. Browser-based Admin UI **must** use OIDC.
API key authentication is supported for CLI/server-to-server automation only.

```bash
# OIDC (recommended, required for browser UI)
export HONUA_ADMIN_TOKEN="your-oidc-access-token"

# Use OIDC token for admin endpoints
curl "http://localhost:8080/api/v1/admin/version" \
  -H "Authorization: Bearer $HONUA_ADMIN_TOKEN"
```

```bash
# API key (automation only, not for browser UI)
export HONUA_ADMIN_PASSWORD="your-secure-password"

curl "http://localhost:8080/api/v1/admin/version" \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD"
```

### Admin Endpoints (v1)

All admin endpoints are now versioned under `/api/v1/admin/*` for stability and headless client support.
Examples below use API key headers for CLI usage; replace with `Authorization: Bearer $HONUA_ADMIN_TOKEN`
for OIDC clients.

#### Table Discovery

```bash
# List available tables
curl "http://localhost:8080/api/v1/admin/connections/test/tables" \
  -H "X-API-Key: your-secure-password"
```

#### Admin Metadata API v1 - Resource Model

```bash
# Server version + supported metadata API versions
curl "http://localhost:8080/api/v1/admin/version" \
  -H "X-API-Key: your-secure-password"

curl "http://localhost:8080/api/v1/admin/capabilities" \
  -H "X-API-Key: your-secure-password"

# List metadata resources (filter by kind/namespace)
curl "http://localhost:8080/api/v1/admin/metadata/resources?kind=Layer&namespace=default" \
  -H "X-API-Key: your-secure-password"

# Create a Layer metadata resource
curl -X POST "http://localhost:8080/api/v1/admin/metadata/resources" \
  -H "X-API-Key: your-secure-password" \
  -H "Content-Type: application/json" \
  -d '{
    "apiVersion": "honua.io/v1alpha1",
    "kind": "Layer",
    "metadata": {
      "name": "parcels",
      "namespace": "default",
      "labels": { "env": "dev" }
    },
    "spec": {
      "tableName": "parcels",
      "schemaName": "public",
      "geometryType": "Polygon",
      "srid": 4326
    }
  }'

# Update a resource (If-Match required)
etag=$(curl -sI "http://localhost:8080/api/v1/admin/metadata/resources/Layer/default/parcels" \
  -H "X-API-Key: your-secure-password" | awk '/ETag/ {print $2}' | tr -d '\r')

curl -X PUT "http://localhost:8080/api/v1/admin/metadata/resources/Layer/default/parcels" \
  -H "X-API-Key: your-secure-password" \
  -H "If-Match: $etag" \
  -H "Content-Type: application/json" \
  -d '{
    "apiVersion": "honua.io/v1alpha1",
    "kind": "Layer",
    "metadata": {
      "name": "parcels",
      "namespace": "default"
    },
    "spec": {
      "tableName": "parcels",
      "schemaName": "public",
      "geometryType": "Polygon",
      "srid": 4326,
      "description": "Updated parcel description"
    }
  }'

# Delete a resource (If-Match required)
curl -X DELETE "http://localhost:8080/api/v1/admin/metadata/resources/Layer/default/parcels" \
  -H "X-API-Key: your-secure-password" \
  -H "If-Match: $etag"
```

#### Admin Metadata API v1 - Manifest (GitOps)

```bash
# Export a full manifest snapshot
curl "http://localhost:8080/api/v1/admin/manifest" \
  -H "X-API-Key: your-secure-password"

# Apply a manifest (supports dryRun/prune)
curl -X POST "http://localhost:8080/api/v1/admin/manifest/apply" \
  -H "X-API-Key: your-secure-password" \
  -H "Content-Type: application/json" \
  -d '{
    "dryRun": true,
    "prune": false,
    "resources": [
      {
        "apiVersion": "honua.io/v1alpha1",
        "kind": "Layer",
        "metadata": { "name": "parcels", "namespace": "default" },
        "spec": {
          "tableName": "parcels",
          "schemaName": "public",
          "geometryType": "Polygon",
          "srid": 4326
        }
      }
    ]
  }'
```

#### Admin Import API v1

```bash
# Get supported file formats
curl "http://localhost:8080/api/v1/admin/import/formats" \
  -H "X-API-Key: your-secure-password"

# Preview a file before import
curl -X POST "http://localhost:8080/api/v1/admin/import/preview" \
  -H "X-API-Key: your-secure-password" \
  -F "file=@parcels.geojson"

# Import a file
curl -X POST "http://localhost:8080/api/v1/admin/import/upload" \
  -H "X-API-Key: your-secure-password" \
  -F "File=@parcels.geojson" \
  -F "TableName=imported_parcels" \
  -F "TargetSrid=4326" \
  -F "OverwriteExisting=true"

# Get import limits
curl "http://localhost:8080/api/v1/admin/import/limits" \
  -H "X-API-Key: your-secure-password"

# Get active import jobs
curl "http://localhost:8080/api/v1/admin/import/jobs" \
  -H "X-API-Key: your-secure-password"

# Get job status
curl "http://localhost:8080/api/v1/admin/import/jobs/{jobId}" \
  -H "X-API-Key: your-secure-password"

# Cancel an import job
curl -X POST "http://localhost:8080/api/v1/admin/import/jobs/{jobId}/cancel" \
  -H "X-API-Key: your-secure-password"
```

### Versioned Admin Endpoints

All admin import endpoints are versioned under `/api/v1/admin/import/*`:

```bash
curl "http://localhost:8080/api/v1/admin/import/formats" \
  -H "X-API-Key: your-secure-password"
```

### Cache Invalidation

Metadata updates automatically invalidate:
- Redis/in-memory cache for layer and service metadata
- Output cache with `metadata` tag (service/layer responses)

This ensures clients always receive fresh data after administrative changes.

## Error Handling

### Common HTTP Status Codes

- **200 OK**: Successful request
- **201 Created**: Feature created successfully
- **204 No Content**: Feature updated/deleted successfully
- **400 Bad Request**: Invalid request parameters or syntax
- **401 Unauthorized**: Missing or invalid token or API key (admin endpoints)
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
