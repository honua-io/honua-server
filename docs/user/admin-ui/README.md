# Admin UI Documentation

The Honua Admin UI provides a web-based interface for managing your geospatial data services. Access it at `/admin` on your Honua Server instance.

## 🚀 **Getting Started**

1. **Start Honua Server**: `docker compose up -d`
2. **Open Admin UI**: Navigate to `http://localhost:8080/admin`
3. **Begin setup**: Start with [database connections](#database-connections)

## 📚 **Workflows**

### **Essential Setup**
1. [**Database Connections**](connections-guide.md) - Connect to your PostGIS databases
2. [**Layer Publishing**](layers-guide.md) - Publish database tables as web services
3. [**Data Import**](import-guide.md) - Import files or Esri services

### **Advanced Features**
4. [**Style Editor**](styles-guide.md) - Create custom map styles with Maputnik
5. [**Map Preview**](preview-guide.md) - Preview your data and styles
6. [**Health Monitoring**](health-guide.md) - Monitor system health and performance

## 🎯 **Quick Tasks**

| I want to... | Go to |
|---------------|-------|
| **Add a database** | [Connections Guide](connections-guide.md#adding-connections) |
| **Publish a table as a layer** | [Layer Publishing](layers-guide.md#publishing-layers) |
| **Import a GeoJSON file** | [File Import](import-guide.md#file-import) |
| **Import an Esri service** | [Service Import](import-guide.md#esri-service-import) |
| **Style my data** | [Style Editor](styles-guide.md#editing-styles) |
| **Preview on a map** | [Map Preview](preview-guide.md#viewing-data) |
| **Check system health** | [Health Dashboard](health-guide.md#monitoring-health) |

## 📱 **Navigation**

The Admin UI uses a sidebar navigation with these main sections:

- **🏠 Dashboard** - Health and system overview
- **🔌 Connections** - Database connection management
- **📄 Layers** - Published layer management
- **🎨 Styles** - Map style editor (Maputnik)
- **🗺️ Preview** - Data and map preview
- **📥 Import** - File and service import

## 🔐 **Authentication**

The Admin UI requires authentication when security is enabled. See [Security Configuration](../../devops/SECURITY_CONFIGURATION.md) for setup details.

## 🛠️ **Troubleshooting**

**Common Issues:**
- **Can't connect to database**: Check connection parameters in [Connections Guide](connections-guide.md#troubleshooting-connections)
- **Layer not appearing**: Verify layer is enabled in [Layer Management](layers-guide.md#managing-layers)
- **Import failed**: Check import logs in [Import Guide](import-guide.md#troubleshooting-imports)
- **Map not loading**: Verify data and styles in [Preview Guide](preview-guide.md#troubleshooting-preview)

For system-level issues, see [DevOps Troubleshooting](../../devops/troubleshooting/).

## 🔗 **Related Documentation**

- [Server Management API](../CONTROL_PLANE_API.md) - Admin automation
- [Geospatial Data APIs](../STANDARDS_APIS.md) - Using published services
- [Geospatial API Examples](../API_EXAMPLES.md) - Data integration examples

---
*For deployment and configuration, see [DevOps Documentation](../../devops/)*