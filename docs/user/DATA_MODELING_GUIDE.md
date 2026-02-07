# Spatial Data Modeling Guide

This guide covers best practices for designing, structuring, and optimizing spatial data for use with Honua Server. Follow these recommendations to ensure optimal performance, maintainability, and interoperability.

## 🎯 **Data Modeling Principles**

### **1. PostGIS-First Design**
Design your data model with PostGIS as the primary data store:
- Use native PostGIS geometry types (`geometry`, `geography`)
- Leverage PostGIS functions for spatial operations
- Design for spatial indexing and performance
- Consider coordinate system requirements early

### **2. Multi-Protocol Compatibility**
Ensure your data works across all Honua protocols:
- Use standard field names and types
- Avoid protocol-specific constraints
- Design for both reading and editing workflows
- Consider field mapping across different standards

### **3. Performance-Oriented Structure**
Structure data for efficient queries and operations:
- Implement proper indexing strategies
- Optimize geometry complexity for use case
- Consider data partitioning for large datasets
- Design for caching effectiveness

## 🗄️ **Database Schema Design**

### **Table Structure Best Practices**

#### **Primary Keys**
```sql
-- Use sequential integer primary keys
CREATE TABLE parcels (
    id SERIAL PRIMARY KEY,  -- Required for Honua
    parcel_id VARCHAR(50) UNIQUE NOT NULL,  -- Business identifier
    geom GEOMETRY(Polygon, 4326) NOT NULL,
    -- other fields...
);
```

**Key Requirements:**
- ✅ **Integer primary key** required for FeatureServer compatibility
- ✅ **Sequential/auto-incrementing** recommended for performance
- ✅ **Single column** primary keys only
- ✅ **Business identifiers** should be separate unique fields

#### **Geometry Columns**
```sql
-- Standard geometry column setup
ALTER TABLE parcels
ADD COLUMN geom GEOMETRY(Polygon, 4326);

-- Add spatial index
CREATE INDEX idx_parcels_geom
ON parcels USING GIST (geom);

-- Add constraint for geometry type validation
ALTER TABLE parcels
ADD CONSTRAINT enforce_geom_type
CHECK (ST_GeometryType(geom) = 'ST_Polygon');
```

**Geometry Best Practices:**
- ✅ **Explicit geometry type** (Point, LineString, Polygon, etc.)
- ✅ **Consistent SRID** within each table
- ✅ **Spatial index** on all geometry columns
- ✅ **Geometry validation** constraints
- ✅ **Standardized column naming** (geom, geometry, the_geom)

### **Field Naming and Types**

#### **Recommended Field Naming**
```sql
CREATE TABLE infrastructure (
    id SERIAL PRIMARY KEY,

    -- Business identifiers
    asset_id VARCHAR(50) UNIQUE NOT NULL,
    asset_type VARCHAR(100) NOT NULL,

    -- Descriptive fields
    name VARCHAR(255),
    description TEXT,
    status VARCHAR(50) DEFAULT 'active',

    -- Numeric measurements
    length_meters DECIMAL(10,2),
    area_sqm DECIMAL(12,2),
    elevation_m DECIMAL(8,2),

    -- Temporal fields
    date_installed DATE,
    date_inspected TIMESTAMP WITH TIME ZONE,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

    -- Geometry
    geom GEOMETRY(Point, 4326) NOT NULL
);
```

**Naming Guidelines:**
- ✅ **snake_case** for field names (best cross-protocol compatibility)
- ✅ **Descriptive names** that are clear across protocols
- ✅ **Units in field names** for measurements (length_meters, area_sqm)
- ✅ **Consistent prefixes** for related fields
- ✅ **Standard temporal fields** (created_at, updated_at)

#### **Data Type Mapping**

| Use Case | PostgreSQL Type | OData EDM Type | GeoJSON Type | Esri JSON Type | Notes |
|----------|-----------------|----------------|--------------|----------------|-------|
| **Text Identifiers** | VARCHAR(50) | Edm.String | string | esriFieldTypeString | Short, indexed fields |
| **Long Text** | TEXT | Edm.String | string | esriFieldTypeString | Descriptions, comments |
| **Integers** | INTEGER | Edm.Int32 | number | esriFieldTypeInteger | Counts, flags, enums |
| **Large Integers** | BIGINT | Edm.Int64 | number | esriFieldTypeOID | IDs, timestamps |
| **Decimals** | DECIMAL(p,s) | Edm.Decimal | number | esriFieldTypeDouble | Precise measurements |
| **Floats** | DOUBLE PRECISION | Edm.Double | number | esriFieldTypeDouble | Scientific data |
| **Dates** | DATE | Edm.Date | string | esriFieldTypeDate | Date-only values |
| **Timestamps** | TIMESTAMP WITH TIME ZONE | Edm.DateTimeOffset | string | esriFieldTypeDate | Full date/time |
| **Booleans** | BOOLEAN | Edm.Boolean | boolean | esriFieldTypeString | True/false flags (Esri uses text) |
| **JSON Data** | JSONB | Edm.String | object | esriFieldTypeString | Structured attributes as JSON text |

### **Advanced Schema Patterns**

#### **Versioning and Change Tracking**
```sql
-- Temporal table for change tracking
CREATE TABLE parcel_history (
    id SERIAL PRIMARY KEY,
    parcel_id INTEGER NOT NULL,
    version_number INTEGER NOT NULL,
    valid_from TIMESTAMP WITH TIME ZONE NOT NULL,
    valid_to TIMESTAMP WITH TIME ZONE,
    changed_by VARCHAR(255),
    change_reason TEXT,

    -- All parcel fields...
    parcel_code VARCHAR(50),
    owner_name VARCHAR(255),
    area_sqm DECIMAL(12,2),
    geom GEOMETRY(Polygon, 4326),

    UNIQUE(parcel_id, version_number)
);

-- Current view for latest versions
CREATE VIEW parcels_current AS
SELECT * FROM parcel_history
WHERE valid_to IS NULL;
```

#### **Hierarchical Data**
```sql
-- Administrative boundaries with hierarchy
CREATE TABLE admin_boundaries (
    id SERIAL PRIMARY KEY,
    boundary_code VARCHAR(50) UNIQUE NOT NULL,
    boundary_name VARCHAR(255) NOT NULL,
    boundary_type VARCHAR(50) NOT NULL, -- 'country', 'state', 'county', 'city'
    parent_id INTEGER REFERENCES admin_boundaries(id),
    level_number INTEGER NOT NULL,
    population INTEGER,
    area_sqkm DECIMAL(12,2),
    geom GEOMETRY(MultiPolygon, 4326) NOT NULL
);

-- Hierarchical index
CREATE INDEX idx_admin_parent ON admin_boundaries(parent_id);
```

## 🗺️ **Coordinate Reference Systems**

### **SRID Selection Strategy**

#### **Common SRID Usage Patterns**
```sql
-- Global data - use WGS84
CREATE TABLE global_points (
    geom GEOMETRY(Point, 4326)  -- WGS84 Geographic
);

-- Regional data - use appropriate projected system
CREATE TABLE us_parcels (
    geom GEOMETRY(Polygon, 3857)  -- Web Mercator for web maps
);

-- Local high-precision data - use local projection
CREATE TABLE survey_points (
    geom GEOMETRY(Point, 2154)  -- Example: RGF93 / Lambert-93 (France)
);
```

#### **Multi-SRID Strategies**
```sql
-- Store in native CRS with web-friendly view
CREATE TABLE precise_parcels (
    id SERIAL PRIMARY KEY,
    geom_native GEOMETRY(Polygon, 2154),  -- High-precision local CRS
    geom_web GEOMETRY(Polygon, 3857)      -- Web Mercator for web clients
);

-- Trigger to maintain synchronized geometries
CREATE OR REPLACE FUNCTION update_web_geometry()
RETURNS TRIGGER AS $$
BEGIN
    NEW.geom_web = ST_Transform(NEW.geom_native, 3857);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER parcels_update_web_geom
    BEFORE INSERT OR UPDATE ON precise_parcels
    FOR EACH ROW EXECUTE FUNCTION update_web_geometry();
```

### **CRS Best Practices**
- ✅ **Choose appropriate precision** for your use case
- ✅ **Document CRS decisions** in schema comments
- ✅ **Consider client requirements** (web maps typically need 3857 or 4326)
- ✅ **Use consistent CRS** within logical data groups
- ✅ **Plan for transformation costs** in high-frequency queries

## 🚀 **Performance Optimization**

### **Indexing Strategies**

#### **Spatial Indexes**
```sql
-- Standard spatial index
CREATE INDEX idx_parcels_geom ON parcels USING GIST (geom);

-- Partial spatial index for active records
CREATE INDEX idx_active_parcels_geom ON parcels USING GIST (geom)
WHERE status = 'active';

-- Combined spatial and attribute index
CREATE INDEX idx_parcels_type_geom ON parcels USING GIST (geom, property_type);
```

#### **Attribute Indexes**
```sql
-- Frequently queried fields
CREATE INDEX idx_parcels_owner ON parcels (owner_name);
CREATE INDEX idx_parcels_type ON parcels (property_type);
CREATE INDEX idx_parcels_status ON parcels (status);

-- Composite indexes for common query patterns
CREATE INDEX idx_parcels_type_status ON parcels (property_type, status);

-- Temporal queries
CREATE INDEX idx_parcels_created ON parcels (created_at);
CREATE INDEX idx_parcels_updated ON parcels (updated_at);
```

### **Geometry Optimization**

#### **Simplification Strategies**
```sql
-- Create simplified versions for different zoom levels
CREATE TABLE parcels_simplified AS
SELECT
    id,
    parcel_id,
    property_type,
    ST_SimplifyPreserveTopology(geom, 0.0001) as geom_detailed,  -- Zoom 15+
    ST_SimplifyPreserveTopology(geom, 0.001) as geom_medium,     -- Zoom 10-14
    ST_SimplifyPreserveTopology(geom, 0.01) as geom_simple       -- Zoom 5-9
FROM parcels;

-- Use appropriate geometry based on map scale
CREATE VIEW parcels_adaptive AS
SELECT
    id, parcel_id, property_type,
    CASE
        WHEN current_setting('app.zoom_level', true)::int >= 15 THEN geom_detailed
        WHEN current_setting('app.zoom_level', true)::int >= 10 THEN geom_medium
        ELSE geom_simple
    END as geom
FROM parcels_simplified;
```

#### **Clustering and Partitioning**
```sql
-- Cluster table by spatial index for better I/O
CLUSTER parcels USING idx_parcels_geom;

-- Partition large tables by geography
CREATE TABLE parcels_by_region (
    LIKE parcels INCLUDING ALL
) PARTITION BY HASH (ST_GeoHash(geom, 5));

-- Create partitions
CREATE TABLE parcels_partition_1 PARTITION OF parcels_by_region
FOR VALUES WITH (modulus 4, remainder 0);
-- ... create additional partitions
```

### **Query Optimization Patterns**

#### **Efficient Spatial Queries**
```sql
-- Use spatial index with bounding box pre-filter
SELECT * FROM parcels
WHERE ST_DWithin(geom, ST_Point(-122.4, 37.8), 1000)  -- 1km radius
AND ST_Intersects(geom, ST_Buffer(ST_Point(-122.4, 37.8), 1000));

-- Avoid expensive operations on large geometries
SELECT id, parcel_id, ST_Area(geom) as area
FROM parcels
WHERE ST_Area(geom) BETWEEN 1000 AND 10000  -- Filter first
AND ST_Within(geom, $1);  -- Then spatial filter
```

#### **Attribute Query Optimization**
```sql
-- Use partial indexes effectively
SELECT * FROM parcels
WHERE status = 'active'  -- Uses partial index
AND property_type = 'residential';

-- Optimize text searches
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX idx_parcels_owner_gin ON parcels USING GIN (owner_name gin_trgm_ops);

-- Efficient text search
SELECT * FROM parcels
WHERE owner_name % 'john smith'  -- Similarity search
ORDER BY similarity(owner_name, 'john smith') DESC;
```

## 🔄 **Data Integration Patterns**

### **ETL Best Practices**

#### **Staging and Validation**
```sql
-- Staging table for imports
CREATE TABLE parcels_staging (
    import_id UUID DEFAULT gen_random_uuid(),
    source_file VARCHAR(255),
    import_date TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

    -- Raw source fields
    raw_parcel_id VARCHAR(100),
    raw_owner VARCHAR(255),
    raw_geometry TEXT,  -- WKT or GeoJSON

    -- Validation results
    is_valid BOOLEAN,
    validation_errors TEXT[],

    -- Transformed fields
    parcel_id VARCHAR(50),
    owner_name VARCHAR(255),
    geom GEOMETRY(Polygon, 4326)
);

-- Validation function
CREATE OR REPLACE FUNCTION validate_parcel_staging()
RETURNS TRIGGER AS $$
BEGIN
    NEW.validation_errors = ARRAY[]::TEXT[];
    NEW.is_valid = true;

    -- Validate parcel ID
    IF NEW.raw_parcel_id IS NULL OR LENGTH(trim(NEW.raw_parcel_id)) = 0 THEN
        NEW.validation_errors = array_append(NEW.validation_errors, 'Missing parcel ID');
        NEW.is_valid = false;
    END IF;

    -- Validate geometry
    IF NEW.geom IS NULL OR NOT ST_IsValid(NEW.geom) THEN
        NEW.validation_errors = array_append(NEW.validation_errors, 'Invalid geometry');
        NEW.is_valid = false;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
```

#### **Change Detection and Synchronization**
```sql
-- Change tracking for sync operations
CREATE TABLE parcel_changes (
    id SERIAL PRIMARY KEY,
    parcel_id INTEGER NOT NULL,
    change_type VARCHAR(20) NOT NULL,  -- 'INSERT', 'UPDATE', 'DELETE'
    change_date TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    old_values JSONB,
    new_values JSONB,
    sync_status VARCHAR(20) DEFAULT 'pending'  -- 'pending', 'synced', 'failed'
);

-- Trigger to capture changes
CREATE OR REPLACE FUNCTION track_parcel_changes()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO parcel_changes (parcel_id, change_type, new_values)
        VALUES (NEW.id, 'INSERT', to_jsonb(NEW));
        RETURN NEW;
    ELSIF TG_OP = 'UPDATE' THEN
        INSERT INTO parcel_changes (parcel_id, change_type, old_values, new_values)
        VALUES (NEW.id, 'UPDATE', to_jsonb(OLD), to_jsonb(NEW));
        RETURN NEW;
    ELSIF TG_OP = 'DELETE' THEN
        INSERT INTO parcel_changes (parcel_id, change_type, old_values)
        VALUES (OLD.id, 'DELETE', to_jsonb(OLD));
        RETURN OLD;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;
```

## 📊 **Protocol-Specific Considerations**

### **FeatureServer Optimization**
```sql
-- FeatureServer works best with:
-- 1. Integer primary keys
-- 2. Standardized field names
-- 3. Proper geometry types

CREATE TABLE esri_compatible_features (
    objectid SERIAL PRIMARY KEY,  -- FeatureServer standard
    globalid UUID DEFAULT gen_random_uuid() UNIQUE,  -- Global identifier

    -- Standard Esri field patterns
    shape GEOMETRY(Polygon, 4326) NOT NULL,
    shape_length DECIMAL(12,4),  -- Auto-calculated
    shape_area DECIMAL(15,6),    -- Auto-calculated

    -- Business fields
    feature_name VARCHAR(255),
    feature_type VARCHAR(100),
    status VARCHAR(50),

    -- Editor tracking (Esri standard)
    created_user VARCHAR(255),
    created_date TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    last_edited_user VARCHAR(255),
    last_edited_date TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Auto-update shape calculations
CREATE OR REPLACE FUNCTION update_shape_metrics()
RETURNS TRIGGER AS $$
BEGIN
    NEW.shape_length = ST_Perimeter(NEW.shape);
    NEW.shape_area = ST_Area(NEW.shape);
    NEW.last_edited_date = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
```

### **OGC API Features Optimization**
```sql
-- OGC API Features benefits from:
-- 1. Clear collection boundaries
-- 2. Standardized metadata
-- 3. Efficient paging support

-- Add collection metadata
COMMENT ON TABLE parcels IS 'Property parcel boundaries with ownership information';
COMMENT ON COLUMN parcels.geom IS 'Property boundary geometry in WGS84';
COMMENT ON COLUMN parcels.owner_name IS 'Current property owner name';

-- Optimize for common OGC query patterns
CREATE INDEX idx_parcels_bbox ON parcels USING GIST (geom);
CREATE INDEX idx_parcels_id_asc ON parcels (id ASC);  -- Efficient paging
```

### **OData v4 Optimization**
```sql
-- OData benefits from:
-- 1. Clear entity relationships
-- 2. Filterable and sortable fields
-- 3. Efficient aggregation support

-- Define clear relationships
CREATE TABLE parcel_owners (
    id SERIAL PRIMARY KEY,
    owner_name VARCHAR(255) NOT NULL,
    contact_email VARCHAR(255),
    contact_phone VARCHAR(50)
);

CREATE TABLE parcels_odata (
    id SERIAL PRIMARY KEY,
    parcel_code VARCHAR(50) UNIQUE NOT NULL,
    owner_id INTEGER REFERENCES parcel_owners(id),
    assessed_value DECIMAL(12,2),
    tax_year INTEGER,
    zoning_code VARCHAR(20),
    area_sqm DECIMAL(12,2),
    geom GEOMETRY(Polygon, 4326) NOT NULL
);

-- Indexes for common OData operations
CREATE INDEX idx_parcels_assessed_value ON parcels_odata (assessed_value);
CREATE INDEX idx_parcels_tax_year ON parcels_odata (tax_year);
CREATE INDEX idx_parcels_zoning ON parcels_odata (zoning_code);
```

## 🔒 **Security and Access Control**

### **Row-Level Security**
```sql
-- Enable row-level security
ALTER TABLE parcels ENABLE ROW LEVEL SECURITY;

-- Policy for data access by region
CREATE POLICY parcels_regional_access ON parcels
FOR ALL TO application_role
USING (
    region_code = ANY(
        SELECT region
        FROM user_regions
        WHERE user_id = current_setting('app.user_id')::uuid
    )
);

-- Policy for read-only public access
CREATE POLICY parcels_public_read ON parcels
FOR SELECT TO public_role
USING (is_public = true);
```

### **Field-Level Security**
```sql
-- Create views for different access levels
CREATE VIEW parcels_public AS
SELECT
    id,
    parcel_code,
    zoning_code,
    area_sqm,
    geom
FROM parcels
WHERE is_public = true;

CREATE VIEW parcels_internal AS
SELECT
    id,
    parcel_code,
    owner_name,
    assessed_value,
    zoning_code,
    area_sqm,
    geom
FROM parcels;
```

## 📋 **Validation and Quality Assurance**

### **Data Quality Functions**
```sql
-- Comprehensive geometry validation
CREATE OR REPLACE FUNCTION validate_geometry_quality(geom geometry)
RETURNS TABLE(
    is_valid boolean,
    error_message text,
    area_sqm decimal,
    perimeter_m decimal,
    vertex_count integer
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        ST_IsValid(geom) as is_valid,
        CASE
            WHEN NOT ST_IsValid(geom) THEN ST_IsValidReason(geom)
            ELSE NULL
        END as error_message,
        ST_Area(geom) as area_sqm,
        ST_Perimeter(geom) as perimeter_m,
        ST_NPoints(geom) as vertex_count;
END;
$$ LANGUAGE plpgsql;

-- Quality constraints
ALTER TABLE parcels ADD CONSTRAINT parcels_min_area
CHECK (ST_Area(geom) >= 1.0);  -- Minimum 1 square meter

ALTER TABLE parcels ADD CONSTRAINT parcels_max_vertices
CHECK (ST_NPoints(geom) <= 10000);  -- Performance constraint
```

### **Automated Quality Monitoring**
```sql
-- Quality metrics view
CREATE VIEW data_quality_metrics AS
SELECT
    'parcels' as table_name,
    COUNT(*) as total_records,
    COUNT(*) FILTER (WHERE ST_IsValid(geom)) as valid_geometries,
    COUNT(*) FILTER (WHERE ST_Area(geom) > 0) as positive_areas,
    AVG(ST_Area(geom)) as avg_area_sqm,
    COUNT(*) FILTER (WHERE owner_name IS NOT NULL) as records_with_owner
FROM parcels;
```

## 🔗 **Related Documentation**

- [**Database Connections**](admin-ui/connections-guide.md) - Connecting to PostGIS databases
- [**Layer Publishing**](admin-ui/layers-guide.md) - Publishing database tables as layers
- [**Performance Monitoring**](../devops/performance-monitoring.md) - Database performance optimization
- [**Query Optimization**](../devops/query-optimization.md) - Advanced query tuning techniques
- [**Geospatial Data APIs**](STANDARDS_APIS.md) - Understanding protocol requirements

---
*Proper data modeling is the foundation of high-performance geospatial applications. Follow these guidelines to ensure your data works optimally across all Honua protocols.*