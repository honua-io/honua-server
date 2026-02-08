# Interactive API Documentation

Honua Server provides comprehensive OpenAPI specifications for all supported protocols. These interactive docs allow you to explore and test the APIs directly.

## 🌐 **Available API Specifications**

### **OGC API Features**
**Protocol**: OGC API Features Parts 1-3
**Base URL**: `/ogc/features`
**OpenAPI Spec**: [ogc-api-features.json](ogc-api-features.json)
**Runtime Spec**: `https://your-honua-server.com/openapi.json`

**What you can do**:
- Browse collections (layers)
- Query features with spatial and attribute filters
- Access individual features
- Perform CRUD operations
- Use advanced CQL2 filtering

{% swagger src="ogc-api-features.json" %}
{% endswagger %}

### **OGC API Tiles**
**Protocol**: OGC API Tiles
**Base URL**: `/ogc/tiles`
**OpenAPI Spec**: [ogc-api-tiles.json](ogc-api-tiles.json)
**Runtime Spec**: `https://your-honua-server.com/ogc/tiles/openapi.json`

**What you can do**:
- Access tilesets metadata
- Retrieve vector tiles (MVT)
- Configure tile parameters
- Access tile matrices

{% swagger src="ogc-api-tiles.json" %}
{% endswagger %}

## ⚙️ **Server Management API**

**Protocol**: REST API
**Base URL**: `/api/v1/admin`
**OpenAPI Spec**: [admin-api.json](admin-api.json)
**Authentication**: API Key or OIDC required

**What you can do**:
- Manage database connections (create, test, list)
- Publish and configure layers from database tables
- Control layer enabling/disabling and protocol settings
- Manage map styles and layer styling
- Monitor system health and observability
- Access recent errors and telemetry status

{% swagger src="admin-api.json" %}
{% endswagger %}

## 🚀 **GeoServices REST FeatureServer + MapServer**

**Protocol**: Esri-compatible REST API
**Base URL**: `/rest/services`
**Compatibility**: 100% ArcGIS compatibility

> **Note**: FeatureServer and MapServer endpoints follow Esri's REST specification and provide self-describing metadata.
>
> For detailed endpoint reference, see the [FeatureServer Coverage Matrix](../user/feature-server-matrix.md) and the [MapServer Coverage Matrix](../user/map-server-matrix.md).

## ⚡ **Vector Tiles (MVT)**

**Protocol**: Mapbox Vector Tiles
**Base URL**: `/tiles`
**Format**: TileJSON metadata + MVT tiles

> **Note**: Vector tile endpoints provide TileJSON metadata for client configuration.
>
> For usage examples, see the [API Examples Guide](../user/API_EXAMPLES.md#vector-tiles-mvt).

## 🧪 **Testing the APIs**

### **Using the Interactive Docs**
1. **Try it out**: Use the "Try it out" buttons in the specs above
2. **Authentication**: Add API keys or tokens as needed
3. **Live server**: Point to your running Honua instance

### **Getting API Specs at Runtime**
```bash
# OGC API Features
curl https://your-honua-server.com/openapi.json

# OGC API Tiles
curl https://your-honua-server.com/ogc/tiles/openapi.json

# Server Management API
curl https://your-honua-server.com/api/v1/admin/openapi.json \
  -H "Authorization: Bearer your-api-key"
```

### **Generate SDK Clients**
```bash
# Generate Python client from OGC API Features
openapi-generator generate \
  -i https://your-honua-server.com/openapi.json \
  -g python \
  -o ./honua-python-client

# Generate C# client from Admin API
openapi-generator generate \
  -i https://your-honua-server.com/api/v1/admin/openapi.json \
  -g csharp \
  -o ./honua-csharp-client
```

## 🔗 **Related Documentation**

- [**Geospatial Data APIs**](../user/STANDARDS_APIS.md) - Protocol overview and selection guide
- [**Server Management API**](../user/CONTROL_PLANE_API.md) - Complete admin API reference
- [**API Examples**](../user/API_EXAMPLES.md) - Code examples for all protocols
- [**Integration Patterns**](../user/INTEGRATION_PATTERNS.md) - Common integration approaches

---
*Interactive API documentation powered by OpenAPI 3.0 specifications auto-generated from the Honua Server codebase.*
