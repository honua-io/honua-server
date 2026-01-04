# Spatial Query Troubleshooting Guide

This guide helps resolve issues with spatial queries, coordinate reference systems (CRS), geometry validation, and spatial indexing in Honua Server.

## Quick Spatial Query Diagnostics

### Test Basic Spatial Functionality

```bash
# Test PostGIS installation
psql -h localhost -U postgres -d honua -c "SELECT PostGIS_Version();"

# Test basic spatial query
psql -h localhost -U postgres -d honua -c "SELECT ST_Point(-122.4194, 37.7749);"

# Check spatial indexes
psql -h localhost -U postgres -d honua -c "
SELECT schemaname, tablename, indexname
FROM pg_indexes
WHERE indexdef LIKE '%gist%'
AND schemaname = 'honua';"

# Test API spatial query
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?geometry=-122.5,37.7,-122.3,37.8&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json"
```

### Verify Spatial Data

```bash
# Check geometry column types
psql -h localhost -U postgres -d honua -c "
SELECT column_name, data_type, udt_name
FROM information_schema.columns
WHERE table_schema = 'honua'
AND table_name = 'features'
AND column_name = 'geometry';"

# Check coordinate reference systems
psql -h localhost -U postgres -d honua -c "
SELECT DISTINCT ST_SRID(geometry) as srid,
       COUNT(*) as feature_count
FROM honua.features
WHERE geometry IS NOT NULL
GROUP BY ST_SRID(geometry);"
```

## Coordinate Reference System (CRS) Issues

### Issue: `Invalid spatial reference identifier` Error

**Error Response**:
```json
{
  "type": "https://honua.app/problems/spatial-reference",
  "title": "Invalid Spatial Reference",
  "status": 400,
  "detail": "SRID 102100 is not recognized. Use EPSG:3857 for Web Mercator."
}
```

**Root Cause**: Using non-standard or unrecognized SRID codes.

**Solutions**:

1. **Use Standard EPSG Codes**:
   ```bash
   # Common spatial reference systems
   # WGS84 (Geographic): EPSG:4326
   # Web Mercator (Projected): EPSG:3857
   # UTM zones: EPSG:326xx (North), EPSG:327xx (South)

   # Correct API calls
   curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?inSR=4326&outSR=4326"
   curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?inSR=3857&outSR=3857"
   ```

2. **Check Available Spatial Reference Systems**:
   ```sql
   -- Find available SRID codes
   SELECT auth_srid, auth_name, srtext
   FROM spatial_ref_sys
   WHERE auth_srid IN (4326, 3857, 2154, 25832)
   ORDER BY auth_srid;

   -- Search for specific projections
   SELECT auth_srid, auth_name, proj4text
   FROM spatial_ref_sys
   WHERE proj4text LIKE '%utm%'
   AND auth_srid BETWEEN 32600 AND 32660
   LIMIT 10;
   ```

3. **Convert Between CRS**:
   ```bash
   # Use OGR to check and convert CRS
   ogrinfo -al input.shp | grep "Coordinate System"

   # Convert to standard CRS
   ogr2ogr -t_srs EPSG:4326 output_wgs84.shp input.shp
   ogr2ogr -t_srs EPSG:3857 output_webmercator.shp input.shp
   ```

### Issue: Incorrect Coordinate Transformations

**Symptoms**:
- Features appear in wrong locations
- Queries return no results when they should
- Geometries appear distorted

**Diagnostic Steps**:

1. **Check Input vs Output CRS**:
   ```bash
   # Test coordinate transformation
   echo "POINT(-122.4194 37.7749)" | gdaltransform -s_srs EPSG:4326 -t_srs EPSG:3857
   # Expected result: POINT(-13627904.42 4544699.80)

   # Reverse transformation
   echo "POINT(-13627904.42 4544699.80)" | gdaltransform -s_srs EPSG:3857 -t_srs EPSG:4326
   # Expected result: POINT(-122.4194 37.7749)
   ```

2. **Verify Database CRS Configuration**:
   ```sql
   -- Check geometry SRIDs in database
   SELECT
       layer_id,
       ST_SRID(geometry) as geometry_srid,
       COUNT(*) as feature_count,
       ST_Extent(geometry) as extent
   FROM honua.features
   WHERE geometry IS NOT NULL
   GROUP BY layer_id, ST_SRID(geometry);

   -- Check if coordinates look reasonable
   SELECT
       ST_X(ST_Centroid(geometry)) as longitude,
       ST_Y(ST_Centroid(geometry)) as latitude
   FROM honua.features
   LIMIT 5;
   ```

**Solutions**:

1. **Standardize on Single CRS**:
   ```sql
   -- Transform all geometries to WGS84
   UPDATE honua.features
   SET geometry = ST_Transform(geometry, 4326)
   WHERE ST_SRID(geometry) != 4326;

   -- Set consistent SRID
   UPDATE honua.features
   SET geometry = ST_SetSRID(geometry, 4326)
   WHERE ST_SRID(geometry) = 0;
   ```

2. **Configure Default CRS for Layers**:
   ```sql
   -- Set default spatial reference for layer
   UPDATE honua.layers
   SET spatial_reference = jsonb_build_object(
       'wkid', 4326,
       'latestWkid', 4326
   )
   WHERE spatial_reference IS NULL;
   ```

3. **Handle CRS in API Requests**:
   ```bash
   # Specify input and output CRS explicitly
   curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?geometry=-13627904,-4544699,-13627800,4544800&geometryType=esriGeometryEnvelope&inSR=3857&outSR=4326&spatialRel=esriSpatialRelIntersects&f=json"
   ```

## Geometry Validation Issues

### Issue: `Invalid geometry` Errors

**Error**: Spatial queries fail with geometry validation errors.

**Diagnostic Steps**:

1. **Check for Invalid Geometries**:
   ```sql
   -- Find invalid geometries
   SELECT
       feature_id,
       layer_id,
       ST_IsValid(geometry) as is_valid,
       ST_IsValidReason(geometry) as validation_issue
   FROM honua.features
   WHERE NOT ST_IsValid(geometry)
   LIMIT 10;

   -- Check specific geometry types
   SELECT
       ST_GeometryType(geometry) as geometry_type,
       COUNT(*) as count,
       COUNT(CASE WHEN NOT ST_IsValid(geometry) THEN 1 END) as invalid_count
   FROM honua.features
   GROUP BY ST_GeometryType(geometry);
   ```

2. **Identify Common Issues**:
   ```sql
   -- Check for self-intersecting polygons
   SELECT feature_id, ST_IsValidReason(geometry)
   FROM honua.features
   WHERE ST_IsValidReason(geometry) LIKE '%self-intersection%';

   -- Check for duplicate points
   SELECT feature_id, ST_NPoints(geometry) as point_count
   FROM honua.features
   WHERE ST_GeometryType(geometry) = 'ST_Polygon'
   AND ST_NPoints(geometry) < 4;  -- Polygons need at least 4 points

   -- Check for extremely small geometries
   SELECT feature_id, ST_Area(geometry) as area
   FROM honua.features
   WHERE ST_Area(geometry) < 1e-10
   AND ST_GeometryType(geometry) LIKE '%Polygon';
   ```

**Solutions**:

1. **Repair Invalid Geometries**:
   ```sql
   -- Fix invalid geometries automatically
   UPDATE honua.features
   SET geometry = ST_MakeValid(geometry)
   WHERE NOT ST_IsValid(geometry);

   -- Alternative: Buffer by zero to fix topology
   UPDATE honua.features
   SET geometry = ST_Buffer(geometry, 0)
   WHERE NOT ST_IsValid(geometry);

   -- Remove extremely small geometries
   DELETE FROM honua.features
   WHERE ST_GeometryType(geometry) LIKE '%Polygon'
   AND ST_Area(geometry) < 1e-12;
   ```

2. **Validate Geometries During Import**:
   ```bash
   # Configure import validation
   export Import__Validation__RepairInvalidGeometries=true
   export Import__Validation__RejectInvalidGeometries=false
   export Import__Validation__MinimumGeometryArea=1e-10
   ```

3. **Prevent Invalid Geometries**:
   ```sql
   -- Add check constraint (optional, for strict validation)
   ALTER TABLE honua.features
   ADD CONSTRAINT features_valid_geometry
   CHECK (ST_IsValid(geometry));

   -- Create trigger to auto-repair (more flexible)
   CREATE OR REPLACE FUNCTION repair_geometry()
   RETURNS TRIGGER AS $$
   BEGIN
       IF NOT ST_IsValid(NEW.geometry) THEN
           NEW.geometry := ST_MakeValid(NEW.geometry);
       END IF;
       RETURN NEW;
   END;
   $$ LANGUAGE plpgsql;

   CREATE TRIGGER features_geometry_repair
   BEFORE INSERT OR UPDATE ON honua.features
   FOR EACH ROW EXECUTE FUNCTION repair_geometry();
   ```

### Issue: Geometry Complexity Performance Problems

**Symptoms**:
- Queries timeout on complex geometries
- High memory usage during spatial operations
- Slow rendering of features

**Solutions**:

1. **Simplify Overly Complex Geometries**:
   ```sql
   -- Check geometry complexity
   SELECT
       layer_id,
       AVG(ST_NPoints(geometry)) as avg_vertices,
       MAX(ST_NPoints(geometry)) as max_vertices,
       COUNT(*) as feature_count
   FROM honua.features
   WHERE geometry IS NOT NULL
   GROUP BY layer_id
   ORDER BY max_vertices DESC;

   -- Simplify complex geometries
   UPDATE honua.features
   SET geometry = ST_SimplifyPreserveTopology(geometry, 0.0001)
   WHERE ST_NPoints(geometry) > 10000;

   -- Alternative: Use adaptive simplification
   UPDATE honua.features
   SET geometry = ST_SimplifyPreserveTopology(
       geometry,
       CASE
           WHEN ST_NPoints(geometry) > 50000 THEN 0.001
           WHEN ST_NPoints(geometry) > 10000 THEN 0.0005
           ELSE 0.0001
       END
   )
   WHERE ST_NPoints(geometry) > 5000;
   ```

2. **Configure Geometry Limits**:
   ```bash
   # Set application-level limits
   export Limits__Geometry__MaxVertices=10000
   export Limits__Geometry__MaxPolygons=100
   export Limits__Geometry__MaxCoordinatePrecision=6
   export Limits__Geometry__SimplificationTolerance=0.0001
   ```

3. **Use Level-of-Detail (LOD) Strategy**:
   ```sql
   -- Create simplified versions for different zoom levels
   ALTER TABLE honua.features ADD COLUMN geometry_simplified geometry;

   UPDATE honua.features
   SET geometry_simplified = ST_SimplifyPreserveTopology(geometry, 0.001)
   WHERE ST_NPoints(geometry) > 1000;

   CREATE INDEX idx_features_geometry_simplified
   ON honua.features USING gist(geometry_simplified);
   ```

## Spatial Index Performance Issues

### Issue: Slow Spatial Queries Despite Proper Indexing

**Diagnostic Steps**:

1. **Check Query Execution Plans**:
   ```sql
   -- Analyze spatial query performance
   EXPLAIN (ANALYZE, BUFFERS)
   SELECT feature_id, ST_AsGeoJSON(geometry)
   FROM honua.features
   WHERE layer_id = 1
   AND ST_Intersects(geometry, ST_MakeEnvelope(-122.5, 37.7, -122.3, 37.8, 4326));

   -- Look for index usage in the plan:
   -- "Index Scan using idx_features_geom on features" (good)
   -- "Seq Scan on features" (bad - not using spatial index)
   ```

2. **Verify Index Statistics**:
   ```sql
   -- Check index usage statistics
   SELECT
       indexrelname,
       idx_scan,
       idx_tup_read,
       idx_tup_fetch
   FROM pg_stat_user_indexes
   WHERE schemaname = 'honua'
   AND indexrelname LIKE '%geom%';

   -- Check table statistics
   SELECT
       schemaname,
       tablename,
       n_tup_ins,
       n_tup_upd,
       n_tup_del,
       last_analyze
   FROM pg_stat_user_tables
   WHERE schemaname = 'honua';
   ```

**Solutions**:

1. **Rebuild or Create Missing Spatial Indexes**:
   ```sql
   -- Drop and recreate spatial index if performance is poor
   DROP INDEX IF EXISTS idx_features_geom;
   CREATE INDEX CONCURRENTLY idx_features_geom
   ON honua.features USING gist(geometry);

   -- Create compound indexes for common query patterns
   CREATE INDEX CONCURRENTLY idx_features_layer_geom
   ON honua.features USING gist(layer_id, geometry);

   -- Create partial indexes for specific layers
   CREATE INDEX CONCURRENTLY idx_features_layer1_geom
   ON honua.features USING gist(geometry)
   WHERE layer_id = 1;
   ```

2. **Update Table Statistics**:
   ```sql
   -- Update table statistics to help query planner
   ANALYZE honua.features;

   -- Force statistics update
   VACUUM ANALYZE honua.features;

   -- Check if autovacuum is working
   SELECT
       schemaname,
       tablename,
       last_autovacuum,
       last_autoanalyze,
       n_tup_ins + n_tup_upd + n_tup_del as total_changes
   FROM pg_stat_user_tables
   WHERE schemaname = 'honua';
   ```

3. **Optimize PostgreSQL Configuration**:
   ```bash
   # Edit postgresql.conf
   sudo nano /etc/postgresql/14/main/postgresql.conf

   # Increase memory for spatial operations
   work_mem = 64MB                    # Per connection, for sorting/hashing
   shared_buffers = 256MB             # Shared cache
   effective_cache_size = 1GB         # OS cache estimate
   maintenance_work_mem = 256MB       # For index creation
   random_page_cost = 1.1             # For SSD storage

   # Restart PostgreSQL
   sudo systemctl restart postgresql
   ```

### Issue: Query Returns Wrong Results Due to CRS Mismatch

**Symptoms**:
- Spatial intersection queries return unexpected results
- Bounding box queries miss obvious features
- Distance calculations are incorrect

**Solutions**:

1. **Ensure Consistent CRS in Queries**:
   ```sql
   -- Bad: Mixed CRS (geometry in 4326, query envelope in 3857)
   SELECT * FROM honua.features
   WHERE ST_Intersects(geometry, ST_MakeEnvelope(-13627904, 4544699, -13627800, 4544800, 3857));

   -- Good: Transform query envelope to match data CRS
   SELECT * FROM honua.features
   WHERE ST_Intersects(
       geometry,
       ST_Transform(ST_MakeEnvelope(-13627904, 4544699, -13627800, 4544800, 3857), 4326)
   );

   -- Better: Transform data to match query CRS (if query CRS is more appropriate)
   SELECT * FROM honua.features
   WHERE ST_Intersects(
       ST_Transform(geometry, 3857),
       ST_MakeEnvelope(-13627904, 4544699, -13627800, 4544800, 3857)
   );
   ```

2. **Standardize CRS Handling in Application**:
   ```bash
   # Configure default CRS handling
   export Spatial__DefaultInputSrid=4326
   export Spatial__DefaultOutputSrid=4326
   export Spatial__AutoTransform=true
   export Spatial__ValidateSrid=true
   ```

## Advanced Spatial Query Issues

### Issue: Complex Spatial Relationships Not Working

**Error**: Advanced spatial relationships like `CONTAINS`, `WITHIN`, `TOUCHES` return unexpected results.

**Solutions**:

1. **Understand PostGIS Spatial Predicates**:
   ```sql
   -- Test different spatial relationships
   WITH test_geometries AS (
       SELECT
           ST_GeomFromText('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))') as poly1,
           ST_GeomFromText('POLYGON((5 5, 15 5, 15 15, 5 15, 5 5))') as poly2,
           ST_GeomFromText('POINT(2 2)') as point1
   )
   SELECT
       ST_Intersects(poly1, poly2) as intersects,    -- true (overlap)
       ST_Contains(poly1, point1) as contains,       -- true (point inside)
       ST_Within(point1, poly1) as within,           -- true (same as contains)
       ST_Touches(poly1, poly2) as touches,          -- false (they overlap)
       ST_Disjoint(poly1, poly2) as disjoint         -- false (they intersect)
   FROM test_geometries;
   ```

2. **Handle Geometry Precision Issues**:
   ```sql
   -- Use tolerance-based comparisons for floating-point precision
   SELECT * FROM honua.features
   WHERE ST_DWithin(geometry, ST_Point(-122.4194, 37.7749), 0.0001);

   -- Snap geometries to grid for consistent results
   UPDATE honua.features
   SET geometry = ST_SnapToGrid(geometry, 0.000001);
   ```

3. **Optimize Complex Spatial Queries**:
   ```sql
   -- Use bounding box pre-filter for performance
   SELECT * FROM honua.features
   WHERE geometry && ST_MakeEnvelope(-122.5, 37.7, -122.3, 37.8, 4326)  -- Bounding box filter (fast)
   AND ST_Intersects(geometry, ST_GeomFromText('POLYGON((-122.45 37.75, -122.35 37.75, -122.35 37.85, -122.45 37.85, -122.45 37.75))', 4326));  -- Exact test (slower)
   ```

### Issue: 3D/Z-Dimension Spatial Queries

**Error**: Queries involving elevation or 3D coordinates not working as expected.

**Solutions**:

1. **Use 3D-Aware Functions**:
   ```sql
   -- Check if geometries have Z dimension
   SELECT
       feature_id,
       ST_HasZ(geometry) as has_z,
       ST_Z(ST_PointN(ST_ExteriorRing(geometry), 1)) as first_z
   FROM honua.features
   WHERE ST_GeometryType(geometry) = 'ST_Polygon'
   LIMIT 5;

   -- Use 3D distance calculations
   SELECT
       feature_id,
       ST_3DDistance(
           geometry,
           ST_GeomFromEWKT('POINTZ(-122.4194 37.7749 100)')
       ) as distance_3d
   FROM honua.features
   WHERE ST_HasZ(geometry);
   ```

2. **Handle Mixed 2D/3D Geometries**:
   ```sql
   -- Force 2D for consistency
   UPDATE honua.features
   SET geometry = ST_Force2D(geometry)
   WHERE ST_HasZ(geometry);

   -- Or force 3D with zero elevation
   UPDATE honua.features
   SET geometry = ST_Force3D(geometry)
   WHERE NOT ST_HasZ(geometry);
   ```

## Spatial Query Optimization Patterns

### Performance Best Practices

1. **Use Appropriate Geometry Types**:
   ```sql
   -- Use specific geometry types instead of generic 'geometry'
   ALTER TABLE honua.features
   ALTER COLUMN geometry TYPE geometry(MultiPolygon, 4326);

   -- This enables type-specific optimizations
   ```

2. **Implement Spatial Clustering**:
   ```sql
   -- Cluster table by spatial index for better I/O
   CLUSTER honua.features USING idx_features_geom;

   -- Verify clustering
   SELECT
       tablename,
       attname,
       n_distinct,
       correlation
   FROM pg_stats
   WHERE schemaname = 'honua'
   AND tablename = 'features'
   AND attname = 'geometry';
   ```

3. **Use Prepared Statements for Repeated Queries**:
   ```sql
   -- Prepare common spatial query
   PREPARE bbox_query(float8, float8, float8, float8, int) AS
   SELECT feature_id, ST_AsGeoJSON(geometry)
   FROM honua.features
   WHERE layer_id = $5
   AND ST_Intersects(geometry, ST_MakeEnvelope($1, $2, $3, $4, 4326));

   -- Execute with parameters
   EXECUTE bbox_query(-122.5, 37.7, -122.3, 37.8, 1);
   ```

### Monitoring Spatial Query Performance

```sql
-- Create monitoring view for spatial query performance
CREATE OR REPLACE VIEW honua.spatial_query_stats AS
SELECT
    query,
    calls,
    total_time,
    mean_time,
    rows,
    100.0 * shared_blks_hit / nullif(shared_blks_hit + shared_blks_read, 0) AS hit_percent
FROM pg_stat_statements
WHERE query LIKE '%ST_%'
OR query LIKE '%geometry%'
ORDER BY total_time DESC;

-- Monitor spatial index usage
SELECT
    schemaname,
    tablename,
    indexname,
    idx_scan,
    idx_tup_read
FROM pg_stat_user_indexes
WHERE indexname LIKE '%geom%'
ORDER BY idx_scan DESC;
```

## Getting Help

For spatial query issues not covered here:

1. **Collect spatial diagnostics**:
   ```bash
   # Create spatial diagnostic report
   {
       echo "=== PostGIS Version ==="
       psql -h localhost -U postgres -d honua -c "SELECT PostGIS_Version();"

       echo "=== Spatial Reference Systems ==="
       psql -h localhost -U postgres -d honua -c "
       SELECT DISTINCT ST_SRID(geometry), COUNT(*)
       FROM honua.features
       GROUP BY ST_SRID(geometry);"

       echo "=== Geometry Types ==="
       psql -h localhost -U postgres -d honua -c "
       SELECT ST_GeometryType(geometry), COUNT(*)
       FROM honua.features
       GROUP BY ST_GeometryType(geometry);"

       echo "=== Spatial Indexes ==="
       psql -h localhost -U postgres -d honua -c "
       SELECT schemaname, tablename, indexname, indexdef
       FROM pg_indexes
       WHERE indexdef LIKE '%gist%'
       AND schemaname = 'honua';"

       echo "=== Invalid Geometries ==="
       psql -h localhost -U postgres -d honua -c "
       SELECT COUNT(*) as invalid_count,
              COUNT(CASE WHEN ST_IsValid(geometry) THEN 1 END) as valid_count
       FROM honua.features;"
   } > spatial-diagnostic-report.txt
   ```

2. **Include sample queries that demonstrate the issue**
3. **Provide geometry samples in WKT format**
4. **Share coordinate system information (EPSG codes)**
5. **Include query execution plans for performance issues**