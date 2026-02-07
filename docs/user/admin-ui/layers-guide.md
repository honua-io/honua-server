# Layer Publishing Guide

Layer publishing makes your database tables available as web services through multiple protocols (FeatureServer, OGC API Features, OData v4, Vector Tiles). You must have database connections configured before publishing layers.

## 📋 **Prerequisites**

- At least one [database connection](connections-guide.md) configured and healthy
- PostGIS database with spatial tables
- Appropriate database permissions for layer publishing

## 🎯 **Overview Workflow**

1. Navigate to Layers page
2. Browse available database tables
3. Select tables to publish
4. Configure layer settings
5. Enable layers as web services

---

## **Step 1: Navigate to Layers**

Access the layer management page:

1. Open Admin UI at `/admin`
2. Click **📄 Layers** in the sidebar

*🖼️ Screenshot needed: Layers page - showing available and published layers*

---

## **Step 2: Browse Available Tables**

View tables from your connected databases:

1. Select database connection from dropdown
2. Browse list of spatial tables
3. View table metadata (geometry type, row count, bounds)

*🖼️ Screenshot needed: Table browser showing spatial tables with metadata*

### **Table Requirements:**
- Must have a geometry column (PostGIS spatial type)
- Must have a primary key column
- Table must be accessible to the database user

---

## **Step 3: Publish Layer**

Make a table available as a web service:

1. Click **📄 Publish** button for desired table
2. Layer publishing dialog opens
3. Configure layer settings

*🖼️ Screenshot needed: Publish layer dialog with configuration options*

### **Layer Configuration:**
- **Name**: Service layer name (URL-friendly)
- **Title**: Human-readable display name
- **Description**: Optional layer description
- **Geometry Type**: Auto-detected from table
- **SRID**: Spatial reference system (auto-detected)
- **Enable Protocols**: Choose which APIs to expose

### **Protocol Options:**
- ✅ **FeatureServer**: Esri-compatible REST API
- ✅ **OGC API Features**: OGC standard API
- ✅ **OData v4**: Microsoft OData protocol
- ✅ **Vector Tiles**: MVT for web mapping

---

## **Step 4: Configure Layer Properties**

Set advanced layer properties:

### **Spatial Settings:**
- **Bounding Box**: Extent of layer data (auto-calculated)
- **Default CRS**: Coordinate reference system for output
- **Max Features**: Limit for single requests

### **Caching Settings:**
- **Cache Duration**: How long to cache responses
- **Cache Key Strategy**: Cache invalidation approach

*🖼️ Screenshot needed: Advanced layer configuration panel*

---

## **Step 5: Enable and Test Layer**

Activate the layer and verify it works:

1. Click **💾 Save** to publish the layer
2. Layer appears in "Published Layers" list
3. Click **🔍 Test** to verify service endpoints

*🖼️ Screenshot needed: Published layers list with test results*

### **Service Endpoints:**
- **FeatureServer**: `/api/services/{layername}/FeatureServer/0`
- **OGC Features**: `/api/collections/{layername}`
- **OData**: `/api/odata/{layername}`
- **Tiles**: `/api/tiles/{layername}/{z}/{x}/{y}.mvt`

---

## **Step 6: Manage Published Layers**

### **Edit Layer**
1. Click **✏️ Edit** for any published layer
2. Modify layer properties and settings
3. Click **💾 Save** to apply changes

*🖼️ Screenshot needed: Edit layer dialog*

### **Disable Layer**
1. Click **⏸️ Disable** to stop serving the layer
2. Layer remains configured but becomes inaccessible
3. Click **▶️ Enable** to reactivate

### **Delete Layer**
1. Click **🗑️ Delete** to remove layer completely
2. Confirm deletion in dialog
3. All service endpoints become unavailable

*🖼️ Screenshot needed: Layer management actions*

**⚠️ Warning**: Deleting a layer will break any applications consuming its services.

---

## **Layer Status Indicators**

The layers list shows:

- **Name**: Layer service name
- **Title**: Display name
- **Source**: Database connection and table
- **Status**: Publishing status (Enabled/Disabled/Error)
- **Protocols**: Active service types
- **Actions**: Edit, Delete, Enable/Disable, Test buttons

*🖼️ Screenshot needed: Full layers list showing multiple published layers*

---

## 🔧 **Troubleshooting Layer Publishing**

### **Common Layer Issues**

**"Table has no geometry column"**
- Verify table contains PostGIS spatial columns
- Check column types: `SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'your_table';`
- Ensure geometry columns use PostGIS types (geometry, geography)

**"No primary key found"**
- Add primary key to table: `ALTER TABLE your_table ADD PRIMARY KEY (id);`
- Verify primary key exists: `\d your_table` in psql

**"Permission denied"**
- Check database user has SELECT permission on table
- For write operations, ensure INSERT/UPDATE/DELETE permissions
- Verify schema access: `GRANT USAGE ON SCHEMA public TO your_user;`

**"Layer publishing failed"**
- Check database connection is healthy
- Verify table still exists and is accessible
- Review server logs for detailed error messages

### **Performance Considerations**

**Large Tables:**
- Consider adding spatial indices: `CREATE INDEX idx_geom ON your_table USING GIST (geom);`
- Set appropriate max feature limits
- Enable aggressive caching for read-only data

**Complex Geometries:**
- Use simplified geometries for tile services
- Consider geometry generalization for zoom levels
- Monitor response times and adjust limits

---

## **Bulk Layer Operations**

### **Publish Multiple Layers**
1. Select multiple tables using checkboxes
2. Click **📄 Publish Selected** button
3. Configure batch publishing settings
4. Apply settings to all selected tables

*🖼️ Screenshot needed: Bulk publishing interface*

### **Import and Auto-Publish**
When importing data, enable "Auto-publish imported tables" to automatically create layers for new data.

---

## ➡️ **Next Steps**

After publishing layers:

1. **[Style Your Data](styles-guide.md)** - Create custom map styles
2. **[Preview Layers](preview-guide.md)** - View data on interactive maps
3. **[Import More Data](import-guide.md)** - Add additional datasets

---

## 🔗 **Related Documentation**

- [Database Connections](connections-guide.md) - Setting up data sources
- [Data Import](import-guide.md) - Adding data to publish
- [Geospatial Data APIs](../STANDARDS_APIS.md) - Using published services
- [Geospatial API Examples](../API_EXAMPLES.md) - Integration code examples

---
*Layer publishing transforms your spatial data into standards-compliant web services accessible by any GIS client or web application.*