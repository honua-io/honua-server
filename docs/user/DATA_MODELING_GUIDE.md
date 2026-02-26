# Honua Data Modeling Guide

This guide covers Honua-specific requirements for designing database schemas that work optimally across all supported protocols (FeatureServer, MapServer, OGC API Features/Tiles, OData v4, Vector Tiles).

## Honua-Specific Requirements

### **1. Multi-Protocol Field Compatibility**
Honua exposes your data through multiple APIs simultaneously. Your schema must work across all protocols:

**Required for All Protocols:**
- Integer primary key (required for FeatureServer)
- Consistent field naming (snake_case recommended)
- Single geometry column per table
- Proper SRID specification

**Avoid These Patterns:**
- Composite primary keys (breaks FeatureServer)
- Field names with spaces or special characters
- Multiple geometry columns (confuses protocol mapping)
- Reserved keywords as field names (`id`, `objectid`, `shape`)

### **2. Honua Protocol Constraints**
Each protocol has specific requirements that affect your schema design:

| Constraint | FeatureServer | OGC Features | OData v4 | Vector Tiles |
|------------|---------------|--------------|-----------|--------------|
| **Primary Key** | Integer required | Any type | Any type | Integer preferred |
| **Field Names** | Case insensitive | Case sensitive | Case sensitive | snake_case |
| **Geometry Types** | Single type per layer | Any | Any | Simplified preferred |
| **Field Limits** | ~255 chars per field | No limit | No limit | Short names better |

## Honua Schema Requirements

### **Primary Key Pattern (Required)**
```sql
-- ✅ Correct: Works with all Honua protocols
CREATE TABLE parcels (
    id SERIAL PRIMARY KEY,  -- Required for FeatureServer
    parcel_code VARCHAR(50) UNIQUE NOT NULL,  -- Business identifier
    geom GEOMETRY(Polygon, 4326) NOT NULL
);

-- ❌ Incorrect: Breaks FeatureServer protocol
CREATE TABLE bad_parcels (
    parcel_code VARCHAR(50),
    region_code VARCHAR(10),
    PRIMARY KEY (parcel_code, region_code)  -- Composite keys not supported
);
```

### **Geometry Column Pattern (Required)**
```sql
-- ✅ Correct: Single geometry column with explicit type
CREATE TABLE features (
    id SERIAL PRIMARY KEY,
    geom GEOMETRY(Point, 4326) NOT NULL
);

-- ❌ Incorrect: Multiple geometry columns confuse protocol mapping
CREATE TABLE bad_features (
    id SERIAL PRIMARY KEY,
    point_geom GEOMETRY(Point, 4326),
    bbox_geom GEOMETRY(Polygon, 4326)  -- Honua won't know which to use
);
```

### **Field Naming for Cross-Protocol Compatibility**
```sql
-- ✅ Recommended: snake_case works across all protocols
CREATE TABLE infrastructure (
    id SERIAL PRIMARY KEY,
    asset_type VARCHAR(100),          -- Clear, descriptive
    install_date DATE,                -- Units implicit or in name
    length_meters DECIMAL(10,2),      -- Units explicit in name
    is_active BOOLEAN,                -- Clear boolean naming
    geom GEOMETRY(LineString, 4326)
);

-- ❌ Avoid: These cause cross-protocol issues
CREATE TABLE problematic_fields (
    id SERIAL PRIMARY KEY,
    "Asset Type" VARCHAR(100),        -- Spaces require quoting
    Date DATE,                        -- Ambiguous meaning
    Length DECIMAL(10,2),             -- No units specified
    Active INTEGER,                   -- Boolean as integer confusing
    Shape GEOMETRY(LineString, 4326)  -- Reserved in some protocols
);
```

## Protocol-Specific Considerations

### **FeatureServer Requirements**
FeatureServer (Esri-compatible) has the strictest requirements:

```sql
-- ✅ FeatureServer-optimized table
CREATE TABLE esri_compatible (
    objectid SERIAL PRIMARY KEY,        -- Standard Esri field name
    globalid UUID DEFAULT gen_random_uuid(),  -- Esri global identifier

    -- Business fields
    feature_name VARCHAR(255),
    feature_type VARCHAR(100),
    created_date TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

    -- Geometry (Esri often uses 'shape')
    shape GEOMETRY(Polygon, 4326) NOT NULL
);
```

**FeatureServer Constraints:**
- Integer primary key (SERIAL/BIGSERIAL)
- Field names without special characters
- Maximum ~255 characters per text field
- Geometry validation required

### **OData v4 Considerations**
OData benefits from clear entity relationships:

```sql
-- ✅ OData-friendly relational design
CREATE TABLE property_owners (
    id SERIAL PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    email VARCHAR(255)
);

CREATE TABLE properties (
    id SERIAL PRIMARY KEY,
    property_code VARCHAR(50) UNIQUE,
    owner_id INTEGER REFERENCES property_owners(id),  -- Clear FK relationship
    assessed_value DECIMAL(12,2),                     -- Aggregatable field
    geom GEOMETRY(Polygon, 4326)
);
```

### **Honua Data Type Mapping**

| PostgreSQL Type | FeatureServer | OGC Features | OData v4 | Vector Tiles | Notes |
|-----------------|---------------|--------------|-----------|--------------|-------|
| **SERIAL/INTEGER** | esriFieldTypeOID | number | Edm.Int32 | number | Required for primary keys |
| **VARCHAR(n)** | esriFieldTypeString | string | Edm.String | string | Truncated if >255 chars in FeatureServer |
| **TEXT** | esriFieldTypeString | string | Edm.String | string | Performance impact in Vector Tiles |
| **DECIMAL(p,s)** | esriFieldTypeDouble | number | Edm.Decimal | number | Precision maintained |
| **BOOLEAN** | esriFieldTypeString | boolean | Edm.Boolean | boolean | Esri converts to "true"/"false" strings |
| **DATE** | esriFieldTypeDate | string | Edm.Date | string | ISO 8601 format |
| **TIMESTAMP WITH TIME ZONE** | esriFieldTypeDate | string | Edm.DateTimeOffset | string | ISO 8601 with timezone |
| **JSONB** | esriFieldTypeString | object | Edm.String | string | Serialized as JSON string in FeatureServer |
| **GEOMETRY** | esriGeometry | GeoJSON geometry | WKT string | MVT geometry | Protocol-specific encoding |

## Honua Performance Optimization

### **Required Indexes for Honua**
```sql
-- ✅ Essential indexes for all Honua tables
CREATE INDEX idx_features_geom ON features USING GIST (geom);  -- Spatial queries
CREATE INDEX idx_features_id ON features (id);                -- Primary key lookup
```

### **Geometry Optimization for Vector Tiles**
```sql
-- ✅ Simplified geometries for better tile performance
CREATE TABLE simplified_parcels AS
SELECT
    id,
    parcel_code,
    ST_SimplifyPreserveTopology(geom, 0.0001) as geom  -- Zoom-appropriate detail
FROM parcels;
```

**Vector Tile Considerations:**
- Simpler geometries = faster tile generation
- Fewer vertices = smaller tile sizes
- Consider pre-generalized geometry columns for different zoom levels

## Coordinate Reference Systems in Honua

### **Recommended SRID Strategy**
Honua works best with standard web-compatible coordinate systems:

```sql
-- ✅ Recommended: WGS84 for broad compatibility
CREATE TABLE features (
    id SERIAL PRIMARY KEY,
    geom GEOMETRY(Point, 4326)  -- Works with all Honua protocols
);

-- ✅ Alternative: Web Mercator for web mapping
CREATE TABLE web_features (
    id SERIAL PRIMARY KEY,
    geom GEOMETRY(Point, 3857)  -- Optimized for web tiles
);
```

**SRID Considerations:**
- **4326 (WGS84)**: Best for global data, required for some OGC compliance
- **3857 (Web Mercator)**: Optimized for Vector Tiles and web mapping
- **Custom projections**: Work but may require client-side transformation

## Common Pitfalls to Avoid

### **Field Name Conflicts**
```sql
-- ❌ These field names cause problems across protocols:
CREATE TABLE problematic_table (
    id INTEGER,                    -- Conflicts with auto-generated IDs
    objectid VARCHAR(50),          -- Reserved in FeatureServer
    shape TEXT,                    -- Reserved for geometry in FeatureServer
    "field with spaces" VARCHAR,   -- Requires quoting, breaks some clients
    $special VARCHAR,              -- Invalid in some protocols
    CLASS VARCHAR                  -- SQL reserved keyword
);

-- ✅ Use these patterns instead:
CREATE TABLE clean_table (
    table_id SERIAL PRIMARY KEY,   -- Clear, unambiguous
    feature_code VARCHAR(50),      -- Business identifier
    geom GEOMETRY(Point, 4326),    -- Standard geometry name
    field_name VARCHAR,            -- snake_case, no spaces
    category VARCHAR,              -- Clear, unreserved name
    feature_class VARCHAR          -- Avoid reserved keywords
);
```

### **Performance Anti-Patterns**
```sql
-- ❌ Missing spatial index (kills performance)
CREATE TABLE slow_features (
    id SERIAL PRIMARY KEY,
    geom GEOMETRY(Point, 4326)
    -- Missing: CREATE INDEX idx_slow_features_geom ON slow_features USING GIST (geom);
);

-- ❌ Complex geometries in Vector Tiles
INSERT INTO features (geom) VALUES (
    ST_GeomFromText('POLYGON((very complex polygon with 50000+ vertices))')
);

-- ✅ Simplified geometries for Vector Tiles
UPDATE features SET
geom = ST_SimplifyPreserveTopology(geom, 0.0001)
WHERE ST_NPoints(geom) > 1000;
```

## Integration with Honua Admin UI

When using the Honua Admin UI to publish layers:

1. **Table Requirements**: Ensure your table has an integer primary key and spatial index
2. **Field Discovery**: Honua will auto-detect field types and suggest appropriate configurations
3. **Geometry Validation**: Tables with invalid geometries will show warnings in the UI
4. **Protocol Enabling**: You can selectively enable/disable protocols per layer based on your schema

```sql
-- ✅ Table ready for Honua Admin UI publishing
CREATE TABLE ready_for_honua (
    id SERIAL PRIMARY KEY,                    -- ✅ Integer PK
    feature_name VARCHAR(255),                -- ✅ Clear field name
    category VARCHAR(100),                    -- ✅ Enumerable values
    created_date TIMESTAMP WITH TIME ZONE,   -- ✅ Temporal data
    is_active BOOLEAN DEFAULT true,          -- ✅ Boolean flag
    geom GEOMETRY(Polygon, 4326) NOT NULL    -- ✅ Valid geometry
);

-- ✅ Required index
CREATE INDEX idx_ready_for_honua_geom ON ready_for_honua USING GIST (geom);
```

## Next Steps

1. **[Admin UI](admin-ui.md)** - Connect to PostGIS and publish layers through the Admin UI
3. **[Geospatial Data APIs](STANDARDS_APIS.md)** - Understand how your data is exposed through different protocols
4. **[API Examples](API_EXAMPLES.md)** - See your data in action through code examples

---
*This guide focuses specifically on Honua Server requirements. For general PostGIS optimization, consult the [official PostGIS documentation](https://postgis.net/docs/).*
