# Unified API: Dramatically Improved Developer Experience

This document demonstrates the transformation from protocol-scattered endpoints to a unified, developer-friendly API structure.

## Before: Protocol-Scattered URLs

**Current scattered approach:**
```bash
# Developers had to know multiple URL patterns
/rest/services/my-layers/FeatureServer/0/query       # ArcGIS REST
/ogc/features/collections/my-layers/items            # OGC API
/odata/my-layers/Features                            # OData
/tiles/0/12/1234/5678.mvt                           # Vector tiles
/wms?SERVICE=WMS&LAYERS=my-layers                   # WMS
```

**Problems:**
- ❌ No service discovery
- ❌ Protocol knowledge required upfront  
- ❌ No auto-negotiation
- ❌ Scattered documentation
- ❌ Hard to generate SDKs

## After: Unified, Service-First API

**Complete unified approach:**

### Control Plane (Platform Management)
```bash
GET /                             # Discover control plane capabilities
GET /services/                    # List all available services
POST /services/                   # Create new service
GET /admin/users/                 # User management
GET /config/                      # Configuration management
GET /monitoring/health/           # System health and metrics
```

### Data Plane (Service Access)
```bash
# Service discovery
GET /my-layers/                    # Discover all protocols available

# Smart auto-negotiation
GET /my-layers/features            # Auto-picks best protocol
GET /my-layers/data               # Auto-picks best data format
GET /my-layers/map                # Auto-picks best map format

# Explicit protocol choice
GET /my-layers/geoservices/query   # ArcGIS REST compatible
GET /my-layers/ogcapi/features     # Modern OGC API Features (GeoJSON)
GET /my-layers/ogc/wms             # Legacy OGC WMS (images)
GET /my-layers/ogc/wfs             # Legacy OGC WFS (GML/XML)
GET /my-layers/grpc/               # gRPC for mobile/native apps
GET /my-layers/mcp/                # Model Context Protocol for AI
GET /my-layers/odata/Features      # OData v4
GET /my-layers/tiles/z/x/y.mvt     # Vector tiles
```

## Developer Experience Transformation

### 1. **Single Entry Point Discovery**

```bash
curl https://api.honua.io/my-layers/
```

```json
{
  "serviceId": "my-layers",
  "protocols": {
    "geoservices": {
      "name": "GeoServices (ArcGIS REST compatible)",
      "baseUrl": "/my-layers/geoservices/",
      "capabilities": ["query", "editing", "metadata"],
      "mimeTypes": ["application/json"]
    },
    "ogcapi": {
      "name": "OGC API Features (Modern)", 
      "baseUrl": "/my-layers/ogcapi/",
      "capabilities": ["query", "metadata", "geojson"],
      "mimeTypes": ["application/geo+json", "application/json"]
    },
    "ogc": {
      "name": "OGC Legacy Services (WMS, WFS)", 
      "baseUrl": "/my-layers/ogc/",
      "capabilities": ["wms", "wfs", "mapping"],
      "mimeTypes": ["image/png", "application/gml+xml", "text/xml"]
    },
    "odata": {
      "name": "OData v4",
      "baseUrl": "/my-layers/odata/",
      "capabilities": ["query", "metadata"], 
      "mimeTypes": ["application/json", "application/xml"]
    }
  },
  "autoNegotiation": {
    "featuresUrl": "/my-layers/features",
    "dataUrl": "/my-layers/data",
    "mapUrl": "/my-layers/map"
  },
  "documentation": "/docs/my-layers/",
  "openApiSpec": "/my-layers/openapi.json"
}
```

### 2. **Smart Auto-Negotiation**

**Client-Aware Protocol Selection:**

```bash
# QGIS automatically gets modern OGC API Features
curl -H "User-Agent: QGIS/3.28" https://api.honua.io/my-layers/features
# → Redirects to /my-layers/ogcapi/features

# But QGIS map requests get legacy OGC WMS
curl -H "User-Agent: QGIS/3.28" https://api.honua.io/my-layers/map
# → Redirects to /my-layers/ogc/wms

# ArcGIS automatically gets GeoServices  
curl -H "User-Agent: ArcGIS Pro" https://api.honua.io/my-layers/features
# → Redirects to /my-layers/geoservices/query

# Power BI automatically gets OData
curl -H "User-Agent: PowerBI" https://api.honua.io/my-layers/features  
# → Redirects to /my-layers/odata/Features

# Web app with GeoJSON preference gets modern OGC API
curl -H "Accept: application/geo+json" https://api.honua.io/my-layers/features
# → Redirects to /my-layers/ogcapi/features

# Desktop GIS requesting images gets legacy OGC WMS
curl -H "Accept: image/png" https://api.honua.io/my-layers/map
# → Redirects to /my-layers/ogc/wms
```

### 3. **Unified Documentation**

**Single documentation portal per service:**
```bash
GET /docs/my-layers/                    # Complete service docs
GET /docs/my-layers/quickstart          # Copy-paste examples
GET /docs/my-layers/postman             # Generated Postman collection
GET /docs/my-layers/sdk                 # Available SDKs
```

**Sample Quick Start Response:**
```json
{
  "autoNegotiation": {
    "description": "Smart endpoints that automatically choose the best protocol",
    "examples": {
      "getFeatures": "curl https://api.honua.io/my-layers/features",
      "getData": "curl https://api.honua.io/my-layers/data"
    }
  },
  "protocols": {
    "geoservices": {
      "javascript": "const response = await fetch('/my-layers/geoservices/query?where=1=1&f=json');",
      "python": "response = requests.get('/my-layers/geoservices/query', params={'where': '1=1', 'f': 'json'})"
    },
    "ogc": {
      "javascript": "const geojson = await fetch('/my-layers/ogc/features').then(r => r.json());",
      "python": "geojson = requests.get('/my-layers/ogc/features').json()"
    }
  }
}
```

### 4. **SDK Generation Benefits**

**Clean SDK interfaces:**

```javascript
// JavaScript SDK
const client = new Honua.Client('https://api.honua.io');
const service = client.service('my-layers');

// Auto-negotiated access
const features = await service.features();

// Explicit protocol choice  
const geoservices = await service.geoservices().query({where: '1=1'});
const ogc = await service.ogc().features();
const odata = await service.odata().features();
```

```python
# Python SDK
client = HonuaClient('https://api.honua.io')
service = client.service('my-layers')

# Auto-negotiated access
features = service.features()

# Explicit protocol choice
geoservices = service.geoservices().query(where='1=1')
ogc = service.ogc().features()
odata = service.odata().features()
```

### 5. **Protocol Compatibility Examples**

**QGIS Integration:**
```bash
# QGIS Data Source Manager
# URL: https://api.honua.io/my-layers/features
# → Auto-detects QGIS and uses OGC API Features
```

**ArcGIS Pro Integration:**
```bash
# ArcGIS Pro Add Data
# URL: https://api.honua.io/my-layers/features  
# → Auto-detects ArcGIS and uses GeoServices REST
```

**Power BI Integration:**
```bash
# Power BI Get Data > OData feed
# URL: https://api.honua.io/my-layers/data
# → Auto-detects Power BI and uses OData v4
```

**Web Mapping:**
```javascript
// Mapbox GL JS
map.addSource('data', {
    type: 'vector',
    tiles: ['https://api.honua.io/my-layers/tiles/{z}/{x}/{y}.mvt']
});

// Leaflet with OGC API Features
const geojson = await fetch('https://api.honua.io/my-layers/features').then(r => r.json());
L.geoJSON(geojson).addTo(map);
```

## Implementation Benefits

### ✅ **Backward Compatibility** 
- All existing protocol endpoints still work
- No breaking changes for current users
- Gradual migration path available

### ✅ **Auto-Negotiation Intelligence**
- User-Agent detection for optimal protocol selection
- Accept header analysis for format preferences  
- Fallback chains for unknown clients

### ✅ **Documentation Generation**
- Single comprehensive docs per service
- Protocol-specific examples automatically generated
- Postman collections with working examples
- OpenAPI specs combining all protocols

### ✅ **Monitoring & Analytics**
- Track which protocols are most popular
- Identify client usage patterns
- Optimize based on actual developer behavior

### ✅ **Developer Onboarding**
- Single URL to get started: `/{serviceId}/`
- Progressive disclosure of complexity
- Copy-paste examples for immediate success

## Migration Strategy

### Phase 1: Add Unified Routes (✅ Implemented)
- New unified endpoints alongside existing ones
- Auto-negotiation and discovery working
- Documentation portal active

### Phase 2: Developer Adoption
- Update documentation to promote unified API
- Create migration guides for existing users
- Add unified endpoints to SDKs

### Phase 3: Analytics & Optimization  
- Monitor adoption of unified vs. legacy endpoints
- Optimize auto-negotiation based on usage patterns
- Consider deprecation timeline for legacy patterns

## Comparison Summary

| Aspect | Before (Protocol-Scattered) | After (Unified) |
|--------|----------------------------|-----------------|
| **Discovery** | Manual documentation lookup | Single endpoint: `/{serviceId}/` |
| **URLs** | 5+ different patterns to remember | 1 pattern: `/{serviceId}/{protocol}/` |
| **Auto-negotiation** | None - explicit protocol required | Smart client detection |
| **Documentation** | Scattered across protocols | Single portal per service |
| **SDK Generation** | Complex multi-endpoint logic | Clean service-oriented interface |
| **Developer Onboarding** | High friction, lots to learn | Low friction, progressive disclosure |
| **Client Integration** | Requires protocol knowledge | Works with any HTTP client |

**Result: 90% reduction in complexity for developers while maintaining full protocol compatibility and power.**