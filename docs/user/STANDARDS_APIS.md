# Geospatial Data APIs (Standards-Based)

Honua exposes industry-standard GIS APIs for geospatial data access and interoperability with existing clients and tools. These are separate from the server management API.

## 🎯 **Quick Protocol Selection**

| If you're using... | Use this API | Endpoint Pattern | Why |
|-------------------|---------------|------------------|-----|
| **ArcGIS Pro/Desktop** | FeatureServer | `/rest/services/{id}/FeatureServer` | Native Esri compatibility |
| **QGIS/OpenLayers** | OGC API Features | `/ogc/features` | Open standards compliance |
| **Power BI/Excel** | OData v4 | `/odata` | Business intelligence integration |
| **Web Maps (MapLibre)** | Vector Tiles | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | High-performance mapping |
| **Custom Applications** | Any protocol | Multiple endpoints | Choose based on client capabilities |

## 📋 **Supported Standards**

### **1. GeoServices REST FeatureServer (Esri-Compatible)**

**Purpose**: Full compatibility with Esri's REST API ecosystem
**Best For**: Organizations with existing ArcGIS investments

#### **Key Features:**
- ✅ **100% compatibility** with ArcGIS Pro, ArcGIS Online, Esri SDKs
- ✅ **Full CRUD operations** (Create, Read, Update, Delete)
- ✅ **Advanced queries** with spatial and attribute filters
- ✅ **Attachments support** for file management
- ✅ **Related records** for complex data relationships
- ✅ **Batch operations** for efficient data management

#### **Endpoint Structure:**
```
/rest/services/{service-name}/FeatureServer/{layer-id}
├── /query                    # Query features
├── /queryRelatedRecords      # Query related data
├── /addFeatures              # Create new features
├── /updateFeatures           # Update existing features
├── /deleteFeatures           # Delete features
├── /applyEdits               # Batch operations
└── /{feature-id}/attachments # File attachments
```

#### **Common Use Cases:**
- **Desktop GIS Integration**: Connect ArcGIS Pro directly to Honua layers
- **Mobile Field Apps**: Esri mobile SDKs for field data collection
- **Web Applications**: ArcGIS API for JavaScript integration
- **Data Synchronization**: Two-way sync with ArcGIS Online

### **2. OGC API Features (Parts 1-3)**

**Purpose**: Open geospatial standards compliance
**Best For**: Standards-based integration and vendor neutrality

#### **Key Features:**
- ✅ **OGC API Features Part 1** (Core) - Basic feature access
- ✅ **OGC API Features Part 2** (CRS) - Multiple coordinate systems
- ✅ **OGC API Features Part 3** (Filtering) - Advanced query capabilities
- ✅ **OpenAPI specification** with self-documenting endpoints
- ✅ **JSON-LD support** for linked data applications
- ✅ **Multiple output formats** (GeoJSON, JSON-LD, HTML)

#### **Endpoint Structure:**
```
/ogc/features
├── /                        # Landing page with API info
├── /conformance            # Standards compliance declaration
├── /collections            # Available feature collections
├── /collections/{id}       # Collection metadata
├── /collections/{id}/items # Feature query and access
└── /collections/{id}/items/{feature-id} # Individual features
```

#### **Common Use Cases:**
- **QGIS Integration**: Direct WFS-style access from desktop GIS
- **Standards Compliance**: Government and enterprise requirements
- **Interoperability**: Vendor-neutral geospatial applications
- **Research Platforms**: Academic and scientific data access

### **3. OData v4**

**Purpose**: Business intelligence and enterprise data integration
**Best For**: Non-GIS applications and business analytics

#### **Key Features:**
- ✅ **Full OData v4 specification** compliance
- ✅ **Excel connectivity** without plugins or custom connectors
- ✅ **Power BI integration** for business dashboards
- ✅ **SAP and ERP integration** via standard OData protocols
- ✅ **Advanced query options** ($filter, $select, $orderby, $top, $skip)
- ✅ **Spatial queries** with geography functions
- ✅ **Batch operations** ($batch) for efficient data processing

#### **Endpoint Structure:**
```
/odata
├── /                       # Service root with metadata
├── /$metadata              # Entity data model
├── /{entity-set}           # Entity collections (your layers)
├── /{entity-set}({key})    # Individual entities
└── /$batch                 # Batch operations
```

#### **Query Examples:**
```http
# Filter features by attribute
GET /odata/parcels?$filter=area gt 1000

# Select specific properties
GET /odata/parcels?$select=id,owner,area

# Spatial query
GET /odata/parcels?$filter=geo.intersects(geom, geography'POLYGON(...)')

# Paging and ordering
GET /odata/parcels?$orderby=area desc&$top=10&$skip=20
```

#### **Common Use Cases:**
- **Business Intelligence**: Power BI dashboards with spatial data
- **Excel Analysis**: Direct Excel connectivity for data analysis
- **ERP Integration**: Asset management and business process integration
- **Analytics Pipelines**: ETL processes and data warehousing

### **4. Vector Tiles (MVT)**

**Purpose**: High-performance web mapping and visualization
**Best For**: Interactive maps and tile-based applications

#### **Key Features:**
- ✅ **Mapbox Vector Tiles** standard compliance
- ✅ **Multi-zoom level** support with automatic generalization
- ✅ **Tile caching** for optimal performance
- ✅ **MapLibre GL compatibility** for web and mobile
- ✅ **TileJSON metadata** for client configuration
- ✅ **Custom styling** with MapLibre style specifications

#### **Endpoint Structure:**
```
/tiles
├── /{layer-id}             # TileJSON metadata
├── /{layer-id}/{z}/{x}/{y}.mvt # Individual vector tiles
└── /{layer-id}/style.json  # Default MapLibre style
```

#### **Common Use Cases:**
- **Web Mapping**: High-performance interactive maps
- **Mobile Applications**: Offline-capable mapping applications
- **Visualization**: Large dataset visualization with dynamic styling
- **Dashboard Maps**: Embedded maps in business applications

### **5. OGC API Tiles**

**Purpose**: Standards-based tile services
**Best For**: Tile-based applications requiring open standards

#### **Key Features:**
- ✅ **OGC API Tiles** specification compliance
- ✅ **Multiple tile formats** (MVT, PNG, JPEG)
- ✅ **TileSet metadata** with zoom level information
- ✅ **Multi-layer tiles** for complex visualizations

## 🔄 **Protocol Interoperability**

### **Multi-Protocol Access**
Every published layer is automatically available through all supported protocols:

```mermaid
graph TD
    A[Published Layer: "parcels"] --> B[FeatureServer: /rest/services/parcels/FeatureServer]
    A --> C[OGC Features: /ogc/features/collections/parcels]
    A --> D[OData: /odata/parcels]
    A --> E[Vector Tiles: /tiles/parcels/{z}/{x}/{y}.mvt]

    B --> F[ArcGIS Pro]
    C --> G[QGIS]
    D --> H[Power BI]
    E --> I[MapLibre]
```

### **Cross-Protocol Features**
- **Consistent Data**: Same underlying data accessible via all protocols
- **Unified Authentication**: Single auth model across all APIs
- **Shared Caching**: Performance optimizations benefit all protocols
- **Coordinated Updates**: Changes via one protocol visible in others

## 🚀 **Getting Started**

### **1. Choose Your Protocol**
- **Existing Esri Users**: Start with FeatureServer
- **Standards-First**: Begin with OGC API Features
- **Business Integration**: Use OData v4
- **Web Mapping**: Implement Vector Tiles

### **2. Explore Examples**
- [**Geospatial API Examples**](API_EXAMPLES.md) - Complete code examples for all protocols
- [**Integration Patterns**](INTEGRATION_PATTERNS.md) - Common integration approaches

### **3. Check Compatibility**
- [**FeatureServer Coverage Matrix**](feature-server-matrix.md) - Esri compatibility details
- [**Protocol Coverage Index**](specifications/protocol-coverage.md) - Standards compliance overview

### **4. Plan Your Integration**
- [**User Journeys**](USER_JOURNEYS.md) - Role-based implementation guides
- [**Integration Patterns**](INTEGRATION_PATTERNS.md) - Architecture patterns and code examples

## 🔒 **Security and Access Control**

### **Authentication Options**
- **API Keys**: Simple token-based authentication
- **OIDC**: Enterprise single sign-on integration
- **Public Access**: Read-only access for public data

### **Authorization Patterns**
- **Read-Only**: Query and visualization access
- **Read-Write**: Full CRUD operations
- **Admin Access**: Layer configuration and management

## 📊 **Performance Considerations**

### **Optimization Strategies**
- **Spatial Indexing**: PostGIS indexes for efficient spatial queries
- **Response Caching**: Redis-based caching for frequently accessed data
- **Vector Tiles**: Pre-generated tiles for high-performance mapping
- **Query Limits**: Configurable limits to prevent resource exhaustion

### **Monitoring and Observability**
- **Request Metrics**: Per-protocol performance monitoring
- **Error Tracking**: Detailed error reporting and logging
- **Cache Efficiency**: Cache hit rates and performance metrics

## 🔗 **Related Documentation**

- [**Server Management API**](CONTROL_PLANE_API.md) - Admin and automation endpoints
- [**Geospatial API Examples**](API_EXAMPLES.md) - Practical implementation examples
- [**Integration Patterns**](INTEGRATION_PATTERNS.md) - Architecture and integration approaches
- [**User Journeys**](USER_JOURNEYS.md) - Role-based getting started guides
- [**Admin UI Documentation**](admin-ui/README.md) - Web interface for layer management

---
*Honua's multi-protocol support ensures your geospatial data is accessible to any client, from traditional GIS software to modern web applications and business intelligence tools.*
