# Server Management API (Control Plane)

The Server Management API powers the Honua Admin UI and can be used directly for **headless automation**
(e.g., provisioning connections, publishing services, importing data, and orchestration). This is separate from the geospatial data access APIs.

## 🎯 **When to Use the Management API**

| Scenario | Example Use Case | Benefits |
|----------|------------------|----------|
| **Platform Integration** | Embed Honua in existing geospatial platform | Seamless user experience |
| **CI/CD Automation** | Auto-publish layers from data pipelines | Continuous deployment |
| **Headless Operations** | Server management without web UI | API-driven workflows |
| **Multi-Tenant Setup** | Programmatically manage customer environments | Scalable operations |
| **Data Orchestration** | Coordinate imports, publishing, and sync | End-to-end automation |

## 🌟 **Key Features**

- ✅ **Connection Management**: Create, test, and manage database connections
- ✅ **Layer Publishing**: Programmatically publish and configure layers
- ✅ **Data Import**: Automate file uploads and Esri service imports
- ✅ **Style Management**: Create and apply custom map styles
- ✅ **Health Monitoring**: Access system health and performance metrics
- ✅ **User Management**: Configure authentication and authorization
- ✅ **Observability**: Access detailed telemetry and operational data

## 📊 **API Structure**

### **Base Endpoints**

| Endpoint | Purpose | Authentication |
|----------|---------|----------------|
| `/api/v1/admin` | Admin API root | Required |
| `/openapi.json` | API specification | Public |
| `/healthz/live` | Liveness check | Public |
| `/healthz/ready` | Readiness check | Public |

### **Core Resource Groups**

```
/api/v1/admin/
├── connections/              # Database connection management
├── layers/                   # Layer publishing and configuration
├── import/                   # Data import operations
├── styles/                   # Map style management
├── observability/            # System monitoring and telemetry
├── performance/              # Performance metrics and tuning
├── security/                 # Authentication and authorization
└── system/                   # System configuration and health
```

## 🔌 **Connection Management**

### **Create Database Connection**
```http
POST /api/v1/admin/connections
Content-Type: application/json

{
  "name": "primary-db",
  "description": "Primary PostGIS database",
  "host": "localhost",
  "port": 5432,
  "database": "honua",
  "username": "postgres",
  "password": "secure-password",
  "sslMode": "Require"
}
```

### **Test Connection Health**
```http
POST /api/v1/admin/connections/{connectionId}/test

Response: 200 OK
{
  "isHealthy": true,
  "responseTimeMs": 45,
  "postgisVersion": "3.3.2",
  "capabilities": ["spatial_indexes", "raster", "topology"]
}
```

### **List Available Tables**
```http
GET /api/v1/admin/connections/{connectionId}/tables

Response: 200 OK
{
  "tables": [
    {
      "name": "parcels",
      "schema": "public",
      "geometryType": "Polygon",
      "srid": 4326,
      "rowCount": 15420,
      "bounds": {
        "minX": -122.5,
        "minY": 37.7,
        "maxX": -122.3,
        "maxY": 37.9
      }
    }
  ]
}
```

## 📄 **Layer Publishing**

### **Publish New Layer**
```http
POST /api/v1/admin/layers
Content-Type: application/json

{
  "name": "city-parcels",
  "title": "City Property Parcels",
  "description": "Municipal property boundaries and ownership",
  "connectionId": "primary-db",
  "tableName": "parcels",
  "geometryColumn": "geom",
  "primaryKeyColumn": "id",
  "protocols": {
    "featureServer": {
      "enabled": true,
      "allowEdits": true,
      "maxRecordCount": 2000
    },
    "ogcFeatures": {
      "enabled": true,
      "maxLimit": 10000
    },
    "odata": {
      "enabled": true,
      "enableBatch": true
    },
    "vectorTiles": {
      "enabled": true,
      "minZoom": 8,
      "maxZoom": 18
    }
  },
  "caching": {
    "enabled": true,
    "ttlSeconds": 300
  }
}
```

### **Configure Layer Security**
```http
PUT /api/v1/admin/layers/{layerId}/security
Content-Type: application/json

{
  "readAccess": "public",
  "writeAccess": "authenticated",
  "adminAccess": "admin",
  "allowedRoles": ["gis_editor", "city_planner"]
}
```

### **Update Layer Metadata**
```http
PATCH /api/v1/admin/layers/{layerId}
Content-Type: application/json

{
  "title": "Updated City Property Parcels",
  "tags": ["property", "municipal", "boundaries"],
  "attribution": "City of Example",
  "license": "CC BY 4.0"
}
```

## 📥 **Data Import Operations**

### **File Upload Import**
```http
POST /api/v1/admin/import/file
Content-Type: multipart/form-data

{
  "file": [binary file data],
  "connectionId": "primary-db",
  "tableName": "new_parcels",
  "options": {
    "createTable": true,
    "overwriteExisting": false,
    "validateGeometry": true,
    "autoPublish": true
  }
}
```

### **Esri Service Import**
```http
POST /api/v1/admin/import/esri-service
Content-Type: application/json

{
  "serviceUrl": "https://services.arcgis.com/.../FeatureServer",
  "connectionId": "primary-db",
  "layers": [0, 1, 2],
  "authentication": {
    "type": "token",
    "token": "your-esri-token"
  },
  "options": {
    "preserveLayerNames": true,
    "batchSize": 1000,
    "autoPublish": true
  }
}
```

### **Monitor Import Progress**
```http
GET /api/v1/admin/import/jobs/{jobId}

Response: 200 OK
{
  "id": "job-12345",
  "status": "in_progress",
  "progress": 0.65,
  "recordsProcessed": 6500,
  "totalRecords": 10000,
  "startTime": "2024-01-15T10:30:00Z",
  "estimatedCompletion": "2024-01-15T10:45:00Z",
  "errors": []
}
```

## 🎨 **Style Management**

### **Create Custom Style**
```http
POST /api/v1/admin/styles
Content-Type: application/json

{
  "name": "parcel-choropleth",
  "title": "Property Value Visualization",
  "description": "Color-coded parcels by assessed value",
  "style": {
    "version": 8,
    "sources": {
      "parcels": {
        "type": "vector",
        "url": "/api/tiles/city-parcels"
      }
    },
    "layers": [
      {
        "id": "parcel-fill",
        "type": "fill",
        "source": "parcels",
        "paint": {
          "fill-color": [
            "interpolate",
            ["linear"],
            ["get", "assessed_value"],
            0, "#ffffcc",
            500000, "#a1dab4",
            1000000, "#41b6c4",
            2000000, "#2c7fb8"
          ],
          "fill-opacity": 0.8
        }
      }
    ]
  }
}
```

### **Apply Style to Layer**
```http
PUT /api/v1/admin/layers/{layerId}/style
Content-Type: application/json

{
  "styleId": "parcel-choropleth",
  "isDefault": true
}
```

## 📊 **Health Monitoring & Observability**

### **System Health Overview**
```http
GET /api/v1/admin/observability/health

Response: 200 OK
{
  "overall": "healthy",
  "components": {
    "database": {
      "status": "healthy",
      "responseTimeMs": 23,
      "activeConnections": 8,
      "maxConnections": 50
    },
    "cache": {
      "status": "healthy",
      "hitRate": 0.87,
      "memoryUsage": "2.1GB",
      "maxMemory": "4GB"
    },
    "storage": {
      "status": "healthy",
      "availableSpace": "45GB",
      "totalSpace": "100GB"
    }
  }
}
```

### **Performance Metrics**
```http
GET /api/v1/admin/observability/metrics?timeRange=1h

Response: 200 OK
{
  "metrics": {
    "requests": {
      "total": 15420,
      "errorsCount": 23,
      "averageResponseTime": 156
    },
    "protocols": {
      "featureServer": {"requests": 8420, "avgResponseTime": 145},
      "ogcFeatures": {"requests": 3200, "avgResponseTime": 167},
      "odata": {"requests": 2100, "avgResponseTime": 189},
      "vectorTiles": {"requests": 1700, "avgResponseTime": 89}
    }
  }
}
```

### **Error Analysis**
```http
GET /api/v1/admin/observability/errors?severity=high&timeRange=24h

Response: 200 OK
{
  "errors": [
    {
      "timestamp": "2024-01-15T14:23:00Z",
      "severity": "high",
      "message": "Database connection timeout",
      "endpoint": "/rest/services/parcels/FeatureServer/0/query",
      "userId": "user-12345",
      "stackTrace": "..."
    }
  ]
}
```

## 🔒 **Security Configuration**

### **Configure Authentication**
```http
PUT /api/v1/admin/security/authentication
Content-Type: application/json

{
  "apiKey": {
    "enabled": true,
    "keys": [
      {
        "name": "automation-key",
        "key": "honua_ak_...",
        "permissions": ["read", "write", "admin"],
        "expiresAt": "2024-12-31T23:59:59Z"
      }
    ]
  },
  "oidc": {
    "enabled": true,
    "authority": "https://auth.example.com",
    "clientId": "honua-server",
    "requiredClaims": {
      "role": "honua_user"
    }
  }
}
```

### **User Role Management**
```http
POST /api/v1/admin/security/roles
Content-Type: application/json

{
  "name": "gis_editor",
  "description": "GIS data editors with write access",
  "permissions": {
    "layers": ["read", "write"],
    "connections": ["read"],
    "import": ["execute"],
    "admin": ["read"]
  }
}
```

## ⚙️ **System Configuration**

### **Update Server Settings**
```http
PUT /api/v1/admin/system/settings
Content-Type: application/json

{
  "limits": {
    "maxFeatureCount": 10000,
    "maxResponseSizeBytes": 104857600,
    "queryTimeoutSeconds": 30
  },
  "caching": {
    "defaultTtlSeconds": 300,
    "maxCacheSizeBytes": 1073741824
  },
  "logging": {
    "level": "Information",
    "enableSqlLogging": false
  }
}
```

### **Backup and Export**
```http
POST /api/v1/admin/system/backup
Content-Type: application/json

{
  "includeData": false,
  "includeConfiguration": true,
  "includeStyles": true,
  "format": "zip"
}

Response: 202 Accepted
{
  "jobId": "backup-67890",
  "downloadUrl": "/api/v1/admin/system/downloads/backup-67890.zip",
  "expiresAt": "2024-01-16T10:30:00Z"
}
```

## 🔄 **Automation Workflows**

### **Complete Layer Publishing Workflow**
```bash
#!/bin/bash

# 1. Create database connection
CONNECTION_ID=$(curl -X POST "$HONUA_URL/api/v1/admin/connections" \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "production-db",
    "host": "prod-postgres",
    "database": "gis",
    "username": "postgres",
    "password": "'"$DB_PASSWORD"'"
  }' | jq -r '.id')

# 2. Test connection
curl -X POST "$HONUA_URL/api/v1/admin/connections/$CONNECTION_ID/test" \
  -H "Authorization: Bearer $API_KEY"

# 3. Publish layer
LAYER_ID=$(curl -X POST "$HONUA_URL/api/v1/admin/layers" \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "automated-parcels",
    "connectionId": "'"$CONNECTION_ID"'",
    "tableName": "parcels",
    "protocols": {
      "featureServer": {"enabled": true},
      "ogcFeatures": {"enabled": true},
      "vectorTiles": {"enabled": true}
    }
  }' | jq -r '.id')

# 4. Verify layer health
curl "$HONUA_URL/api/v1/admin/layers/$LAYER_ID/health" \
  -H "Authorization: Bearer $API_KEY"

echo "Layer published: $LAYER_ID"
```

### **CI/CD Integration Example**
```yaml
# .github/workflows/deploy-layers.yml
name: Deploy GIS Layers

on:
  push:
    paths: ['data/**']

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Import and publish updated data
        run: |
          # Upload new data files
          for file in data/*.geojson; do
            curl -X POST "${{ secrets.HONUA_URL }}/api/v1/admin/import/file" \
              -H "Authorization: Bearer ${{ secrets.HONUA_API_KEY }}" \
              -F "file=@$file" \
              -F "autoPublish=true"
          done

      - name: Verify deployments
        run: |
          curl "${{ secrets.HONUA_URL }}/api/v1/admin/observability/health" \
            -H "Authorization: Bearer ${{ secrets.HONUA_API_KEY }}"
```

## 🚀 **Getting Started**

### **1. Authentication Setup**
- Generate API key in Admin UI or configure OIDC
- Test authentication with `/api/v1/admin/health` endpoint

### **2. Basic Operations**
- Create database connection
- Test connection health
- Browse available tables
- Publish your first layer

### **3. Advanced Workflows**
- Set up automated import pipelines
- Configure custom styling
- Implement monitoring and alerting
- Integrate with existing platforms

## 📋 **OpenAPI Specification**

The complete API specification is available at:
- **Interactive Documentation**: `/openapi.json` (Swagger/OpenAPI 3.0)
- **Admin UI Integration**: Built-in API explorer in Admin UI
- **SDK Generation**: Use OpenAPI spec to generate client SDKs

Example API client generation:
```bash
# Generate Python client
openapi-generator generate -i http://honua.example.com/openapi.json \
  -g python -o ./honua-python-client

# Generate C# client
openapi-generator generate -i http://honua.example.com/openapi.json \
  -g csharp -o ./honua-csharp-client
```

## 🔗 **Related Documentation**

- [**Security Configuration**](../devops/SECURITY_CONFIGURATION.md) - Authentication and authorization setup
- [**Admin UI Documentation**](admin-ui/README.md) - Web interface for visual management
- [**Geospatial Data APIs**](STANDARDS_APIS.md) - Published data access endpoints
- [**Integration Patterns**](INTEGRATION_PATTERNS.md) - Common integration architectures
- [**Deployment Scenarios**](../devops/DEPLOYMENT_SCENARIOS.md) - Infrastructure deployment patterns

---
*The Server Management API enables powerful automation and integration capabilities, making Honua a seamless part of your geospatial infrastructure.*
