# Data Import Guide

Honua Server supports multiple ways to import spatial data: file uploads (GeoJSON, Shapefile, GeoPackage), Esri service imports, and direct database operations. All imported data can be automatically published as web services.

## 📋 **Prerequisites**

- [Database connection](connections-guide.md) configured with write permissions
- Source data in supported formats
- Sufficient database storage space

## 🎯 **Import Methods Overview**

| Method | Use Case | Supported Formats | Auto-Publish |
|--------|----------|-------------------|--------------|
| **File Upload** | Static files | GeoJSON, Shapefile, GeoPackage | ✅ |
| **Esri Service Import** | Live service data | FeatureServer, MapServer | ✅ |
| **Streaming Import** | Large datasets | Same as file upload | ✅ |
| **Database Direct** | Existing data | PostGIS tables | Manual |

---

## **Method 1: File Upload Import**

Upload spatial data files directly through the web interface.

### **Step 1: Navigate to Import**

1. Open Admin UI at `/admin`
2. Click **📥 Import** in the sidebar
3. Select **📁 File Upload** tab

*🖼️ Screenshot needed: Import page with file upload interface*

### **Step 2: Select Files**

1. Click **📁 Choose Files** or drag files to upload area
2. Select one or more spatial data files
3. Files are validated automatically

*🖼️ Screenshot needed: File selection with validation indicators*

### **Supported File Formats:**
- **GeoJSON** (`.geojson`, `.json`) - Web-standard format
- **Shapefile** (`.shp` + required files) - ESRI standard, upload as ZIP
- **GeoPackage** (`.gpkg`) - OGC SQLite-based format
- **KML/KMZ** (`.kml`, `.kmz`) - Google Earth format

### **File Requirements:**
- Maximum file size: 100MB per file
- Shapefile: Must include `.shp`, `.shx`, `.dbf` files (ZIP recommended)
- Valid geometry data required
- UTF-8 encoding recommended

### **Step 3: Configure Import Settings**

Configure how the data will be imported:

*🖼️ Screenshot needed: Import configuration panel*

**Target Database:**
- **Connection**: Choose destination database
- **Schema**: Target schema (default: public)
- **Table Name**: Destination table name (auto-generated from filename)

**Processing Options:**
- **Coordinate System**: Source CRS (auto-detected when possible)
- **Target CRS**: Transform to different CRS if needed
- **Append Mode**: Add to existing table vs. create new table
- **Auto-Publish**: Automatically create layer after import

**Data Handling:**
- **Geometry Validation**: Fix invalid geometries during import
- **Duplicate Handling**: Skip, overwrite, or append duplicate records
- **Field Mapping**: Map source fields to target columns

### **Step 4: Execute Import**

1. Click **📥 Start Import** to begin processing
2. Progress indicator shows import status
3. Import completes with summary report

*🖼️ Screenshot needed: Import progress and completion summary*

### **Import Results:**
- **Records Processed**: Number of features imported
- **Errors**: Any validation or processing errors
- **Warnings**: Data quality issues
- **Performance**: Import duration and rate

---

## **Method 2: Esri Service Import**

Import data from existing Esri FeatureServer or MapServer services.

### **Step 1: Service Import Setup**

1. Navigate to **📥 Import** → **🌐 Service Import** tab
2. Enter Esri service URL
3. Configure authentication if required

*🖼️ Screenshot needed: Service import configuration form*

### **Step 2: Service Discovery**

1. Click **🔍 Discover** to analyze the service
2. Review available layers and metadata
3. Select layers to import

*🖼️ Screenshot needed: Service layer selection interface*

### **Service Types Supported:**
- **FeatureServer**: Vector data with editing capabilities
- **MapServer**: Read-only map services with data access

### **Authentication Options:**
- **None**: Public services
- **Token**: ArcGIS Server token authentication
- **OAuth**: ArcGIS Online OAuth flow
- **API Key**: Developer API keys

### **Step 3: Configure Import**

Set import parameters for selected layers:

**Import Settings:**
- **Layer Selection**: Choose specific layers to import
- **Feature Filtering**: Apply spatial or attribute filters
- **Batch Size**: Records per import batch (performance tuning)
- **Update Mode**: Full refresh vs. incremental updates

**Target Configuration:**
- **Database**: Destination connection
- **Naming**: Table naming convention
- **Schema Mapping**: Field type conversions
- **Auto-Publish**: Create Honua layers automatically

### **Step 4: Execute Service Import**

1. Click **📥 Import Selected Layers**
2. Monitor import progress for each layer
3. Review completion status and any errors

*🖼️ Screenshot needed: Multi-layer import progress dashboard*

---

## **Method 3: Streaming Import**

For large datasets that exceed normal upload limits.

### **Step 1: Enable Streaming**

1. Go to **📥 Import** → **🌊 Streaming** tab
2. Configure streaming parameters
3. Upload large files in chunks

*🖼️ Screenshot needed: Streaming import configuration*

### **Streaming Benefits:**
- **Large Files**: Handle files > 100MB
- **Memory Efficient**: Process without loading entire file
- **Resumable**: Continue interrupted uploads
- **Progress Tracking**: Real-time processing feedback

### **Step 2: Monitor Streaming Progress**

Track import as data streams into database:

*🖼️ Screenshot needed: Streaming import real-time progress*

---

## **Method 4: Database Direct Import**

Work with existing database tables and external import tools.

### **Step 1: External Import**

Use PostGIS tools to import data directly:

```bash
# Import Shapefile with ogr2ogr
ogr2ogr -f PostgreSQL \
    "PG:host=localhost dbname=honua user=postgres" \
    "data.shp" \
    -nln "my_layer" \
    -lco GEOMETRY_NAME=geom

# Import GeoJSON with psql
psql -h localhost -d honua -c "\copy my_table FROM 'data.json'"
```

### **Step 2: Register Imported Tables**

After external import, register tables as Honua layers:

1. Navigate to **📄 Layers** page
2. Refresh available tables list
3. Publish newly imported tables as layers

*🖼️ Screenshot needed: Table registration after external import*

---

## **Import Monitoring and Management**

### **Import History**

View all import operations:

1. Go to **📥 Import** → **📊 History** tab
2. Review past import jobs
3. Access detailed logs and error reports

*🖼️ Screenshot needed: Import history with job details*

### **Import Job Details:**
- **Timestamp**: When import was executed
- **Source**: File name or service URL
- **Target**: Database table destination
- **Status**: Success, Failed, or In Progress
- **Records**: Count of features processed
- **Duration**: Time taken to complete
- **Errors**: Link to error logs

### **Failed Import Recovery**

Handle import failures:

1. Review error logs for specific issues
2. Fix data quality problems in source files
3. Retry import with corrected settings
4. Use partial import for problematic records

---

## 🔧 **Troubleshooting Imports**

### **Common Import Issues**

**"File format not recognized"**
- Verify file has correct extension
- Check file isn't corrupted
- Ensure all required Shapefile components are present
- Try different format (e.g., convert SHP to GeoJSON)

**"Invalid geometry"**
- Enable geometry validation in import settings
- Use ST_MakeValid() for complex polygons
- Check coordinate system matches data

**"Coordinate system not found"**
- Specify source CRS manually
- Verify EPSG code is correct
- Use well-known CRS codes (4326, 3857)

**"Permission denied on target table"**
- Check database user has CREATE TABLE permissions
- Verify schema access permissions
- Ensure sufficient disk space

**"Import timeout"**
- Reduce batch size for large files
- Use streaming import for very large datasets
- Check database connection stability
- Monitor server resources (CPU, memory)

### **Performance Optimization**

**Large File Imports:**
- Use streaming import for files > 100MB
- Import during off-peak hours
- Consider splitting large files
- Monitor database performance during import

**Batch Size Tuning:**
- Default: 1,000 records per batch
- Increase for simple geometries
- Decrease for complex polygons or many attributes
- Monitor memory usage and adjust accordingly

**Database Optimization:**
- Temporarily disable indexes during import
- Rebuild indexes after large imports
- Use VACUUM ANALYZE after imports
- Monitor WAL (Write-Ahead Log) size

---

## **Data Quality and Validation**

### **Geometry Validation**

Honua automatically validates geometries during import:

- **Self-intersecting polygons**: Automatically repaired
- **Invalid ring orientation**: Corrected to follow standards
- **Duplicate vertices**: Removed to reduce size
- **Empty geometries**: Flagged but preserved

### **Attribute Validation**

Data type conversions and validation:

- **Text fields**: UTF-8 encoding enforced
- **Numeric fields**: Invalid values converted to NULL
- **Date fields**: ISO 8601 format preferred
- **Field names**: Sanitized for SQL compatibility

---

## ➡️ **Next Steps**

After importing data:

1. **[Publish as Layers](layers-guide.md)** - Make imported data available as web services
2. **[Style Your Data](styles-guide.md)** - Create custom visualizations
3. **[Preview Data](preview-guide.md)** - View imported data on maps

---

## 🔗 **Related Documentation**

- [Database Connections](connections-guide.md) - Setting up import destinations
- [Layer Publishing](layers-guide.md) - Publishing imported data
- [Geospatial Data APIs](../STANDARDS_APIS.md) - Using imported data via APIs
- [Performance Monitoring](../../devops/performance-monitoring.md) - Monitoring import performance

---
*Data import is the foundation of your geospatial services - ensuring quality data input leads to reliable service output.*