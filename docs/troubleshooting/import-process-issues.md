# Import Process Troubleshooting Guide

This guide helps resolve issues with geospatial data import processes in Honua Server, including file format problems, validation errors, and import progress tracking.

## Quick Import Diagnostics

### Test Import System

```bash
# Check import service status
curl -s http://localhost:8080/health | jq '.components.import'

# Test file upload endpoint
curl -X POST -F "file=@test-data.zip" http://localhost:8080/api/v1/admin/import/upload

# Check import job status
curl -H "X-API-Key: your-api-key" http://localhost:8080/api/v1/admin/import/jobs

# Monitor import logs
docker logs honua-server | grep -i import
```

### Verify Import Prerequisites

```bash
# Check required services
redis-cli ping  # Redis for job management
psql -h localhost -U postgres -d honua -c "SELECT PostGIS_Version();"  # PostGIS

# Check disk space for temporary files
df -h /tmp
df -h /var/lib/honua/uploads

# Verify file permissions
ls -la /var/lib/honua/uploads/
```

## File Format Issues

### Issue: `Unsupported file format` Error

**Error Response**:
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "File format not supported. Supported formats: .zip (Shapefile), .gpkg (GeoPackage), .csv, .geojson"
}
```

**Root Cause**: File doesn't match supported geospatial formats.

**Solutions**:

1. **Verify File Format**:
   ```bash
   # Check file type using file command
   file your-data-file.zip

   # Check file contents for ZIP files
   unzip -l your-data-file.zip

   # Verify shapefile components
   unzip -l shapefile.zip | grep -E '\.(shp|shx|dbf|prj)$'
   ```

2. **Convert Unsupported Formats**:
   ```bash
   # Convert KML to Shapefile using ogr2ogr
   ogr2ogr -f "ESRI Shapefile" output.shp input.kml

   # Convert GeoJSON to Shapefile
   ogr2ogr -f "ESRI Shapefile" output.shp input.geojson

   # Convert CSV to GeoPackage
   ogr2ogr -f GPKG output.gpkg input.csv -oo X_POSSIBLE_NAMES=longitude,lon,x -oo Y_POSSIBLE_NAMES=latitude,lat,y
   ```

3. **Fix Shapefile ZIP Structure**:
   ```bash
   # Correct shapefile ZIP structure
   zip -j corrected-shapefile.zip data.shp data.shx data.dbf data.prj

   # Verify required components
   unzip -l corrected-shapefile.zip | grep -E '\.(shp|shx|dbf)$' | wc -l
   # Should return 3 (minimum required files)
   ```

### Issue: `Malformed ZIP archive` Error

**Root Cause**: Corrupted or improperly structured ZIP file.

**Diagnostic Steps**:

1. **Test ZIP File Integrity**:
   ```bash
   # Test ZIP file
   zip -T your-file.zip

   # Alternative test
   unzip -t your-file.zip

   # Check for password protection
   unzip -l your-file.zip 2>&1 | grep -i password
   ```

2. **Examine ZIP Structure**:
   ```bash
   # List ZIP contents with paths
   unzip -l your-file.zip

   # Check for nested directories
   unzip -l your-file.zip | grep -E '^.*/.*.shp$'
   ```

**Solutions**:

1. **Recreate ZIP Without Nested Directories**:
   ```bash
   # Extract to temporary directory
   mkdir temp_extract
   cd temp_extract
   unzip ../problematic-file.zip

   # Re-zip files at root level
   zip -j ../fixed-shapefile.zip *.shp *.shx *.dbf *.prj
   ```

2. **Fix Character Encoding Issues**:
   ```bash
   # Extract with specific encoding
   unzip -O cp437 your-file.zip  # Windows encoding

   # Alternative using 7zip
   7z x your-file.zip
   ```

### Issue: Missing Spatial Reference System (SRS)

**Error**: `Unable to determine coordinate reference system`

**Root Cause**: Missing or invalid .prj file in shapefile.

**Solutions**:

1. **Add Missing Projection File**:
   ```bash
   # Create .prj file for WGS84 (EPSG:4326)
   echo 'GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563,AUTHORITY["EPSG","7030"]],AUTHORITY["EPSG","6326"]],PRIMEM["Greenwich",0,AUTHORITY["EPSG","8901"]],UNIT["degree",0.0174532925199433,AUTHORITY["EPSG","9122"]],AUTHORITY["EPSG","4326"]]' > data.prj

   # Re-zip with projection file
   zip -j fixed-shapefile.zip data.shp data.shx data.dbf data.prj
   ```

2. **Use GDAL to Add SRS**:
   ```bash
   # Add spatial reference to shapefile
   gdal_edit.py -a_srs EPSG:4326 data.shp

   # Verify SRS was added
   ogrinfo -al data.shp | grep -A 5 "Coordinate System"
   ```

3. **Convert to Known SRS**:
   ```bash
   # Reproject to WGS84
   ogr2ogr -t_srs EPSG:4326 output.shp input.shp

   # Reproject to Web Mercator
   ogr2ogr -t_srs EPSG:3857 output.shp input.shp
   ```

## Data Validation Errors

### Issue: `Invalid geometry detected`

**Error**: Geometry validation failures during import.

**Diagnostic Steps**:

1. **Check Geometry Validity**:
   ```bash
   # Use ogr2ogr to validate geometries
   ogr2ogr -f "ESRI Shapefile" valid_output.shp input.shp -where "OGR_GEOMETRY='VALID'"

   # Check for specific geometry issues
   ogrinfo -al input.shp -sql "SELECT *, ST_IsValid(geometry) as is_valid FROM input"
   ```

2. **Identify Problematic Features**:
   ```sql
   -- After partial import, check invalid geometries in database
   SELECT feature_id, ST_IsValidReason(geometry) as issue
   FROM honua.features
   WHERE NOT ST_IsValid(geometry);
   ```

**Solutions**:

1. **Repair Geometries Before Import**:
   ```bash
   # Fix invalid geometries using ogr2ogr
   ogr2ogr -f "ESRI Shapefile" repaired.shp input.shp \
           -sql "SELECT *, ST_MakeValid(geometry) as geometry FROM input" \
           -dialect SQLite

   # Alternative using GDAL
   ogr2ogr -makevalid repaired.shp input.shp
   ```

2. **Configure Import Validation**:
   ```bash
   # Allow geometry repair during import
   export Import__Validation__RepairInvalidGeometries=true
   export Import__Validation__SkipInvalidFeatures=false

   # Set geometry complexity limits
   export Limits__Geometry__MaxVertices=10000
   export Limits__Geometry__MaxPolygons=100
   ```

3. **Handle Complex Geometries**:
   ```bash
   # Simplify overly complex geometries
   ogr2ogr -simplify 0.0001 simplified.shp complex_input.shp

   # Split multi-part geometries
   ogr2ogr -explodecollections single_parts.shp multi_part_input.shp
   ```

### Issue: `Attribute data type mismatch`

**Error**: Field types don't match expected schema.

**Solutions**:

1. **Examine Field Types**:
   ```bash
   # Check field definitions
   ogrinfo -al input.shp | grep -E "^[A-Z_]+.*:"

   # Get schema information
   ogrinfo -so input.shp layer_name
   ```

2. **Convert Field Types**:
   ```bash
   # Convert string fields to appropriate types
   ogr2ogr -f "ESRI Shapefile" converted.shp input.shp \
           -sql "SELECT CAST(numeric_field AS REAL) as numeric_field, * FROM input"
   ```

3. **Map Field Names**:
   ```bash
   # Rename fields to match schema
   ogr2ogr -f "ESRI Shapefile" mapped.shp input.shp \
           -sql "SELECT old_field_name AS new_field_name, * FROM input"
   ```

## Import Process Failures

### Issue: Import Job Stuck in `Processing` Status

**Root Cause**: Background import process hung or crashed.

**Diagnostic Steps**:

1. **Check Job Status**:
   ```bash
   # Get detailed job status
   curl -H "X-API-Key: your-api-key" \
        "http://localhost:8080/api/v1/admin/import/jobs/JOB_ID" | jq .

   # List all active jobs
   curl -H "X-API-Key: your-api-key" \
        "http://localhost:8080/api/v1/admin/import/jobs?status=processing" | jq .
   ```

2. **Monitor Background Services**:
   ```bash
   # Check Redis for job queue
   redis-cli llen import:queue
   redis-cli lrange import:queue 0 -1

   # Check background service logs
   docker logs honua-server | grep -E "(Import|Background|Job)"
   ```

3. **Check System Resources**:
   ```bash
   # Memory usage
   ps aux | grep dotnet | awk '{sum+=$4} END {print "Memory usage:", sum"%"}'

   # Disk space for temporary files
   df -h /tmp
   du -sh /var/lib/honua/uploads/temp/*
   ```

**Solutions**:

1. **Restart Background Import Service**:
   ```bash
   # Restart the entire application
   docker restart honua-server

   # Or just restart background services (if supported)
   curl -X POST -H "X-API-Key: your-api-key" \
        http://localhost:8080/admin/services/import/restart
   ```

2. **Clear Stuck Jobs**:
   ```bash
   # Clear Redis job queue
   redis-cli del import:queue
   redis-cli del import:processing

   # Reset job status in database
   psql -h localhost -U postgres -d honua -c "
   UPDATE honua.import_jobs
   SET status = 'failed',
       error_message = 'Manually reset due to stuck status',
       completed_at = NOW()
   WHERE status = 'processing'
   AND created_at < NOW() - INTERVAL '1 hour';"
   ```

3. **Clean Up Temporary Files**:
   ```bash
   # Remove old temporary files
   find /var/lib/honua/uploads/temp -type f -mmin +60 -delete

   # Clear old upload files
   find /var/lib/honua/uploads -type f -mtime +7 -delete
   ```

### Issue: `Out of Memory` During Large File Import

**Error**: `OutOfMemoryException` or container killed by OOM killer.

**Solutions**:

1. **Increase Memory Limits**:
   ```bash
   # Docker memory limit
   docker run --memory=4g honua-server

   # Docker Compose
   services:
     honua-server:
       deploy:
         resources:
           limits:
             memory: 4G
   ```

2. **Configure Streaming Import**:
   ```bash
   # Enable streaming import for large files
   export Import__UseStreamingImport=true
   export Import__StreamingBatchSize=1000
   export Import__MaxMemoryUsageMB=512
   ```

3. **Process Large Files in Chunks**:
   ```bash
   # Split large shapefiles before import
   ogr2ogr -f "ESRI Shapefile" chunk_1.shp large_input.shp -where "ROWNUM <= 10000"
   ogr2ogr -f "ESRI Shapefile" chunk_2.shp large_input.shp -where "ROWNUM > 10000 AND ROWNUM <= 20000"
   ```

## Progress Tracking Issues

### Issue: Import Progress Not Updating

**Root Cause**: Progress tracking service not functioning properly.

**Diagnostic Steps**:

1. **Check Progress Updates**:
   ```bash
   # Monitor progress via API
   JOB_ID="your-job-id"
   watch -n 2 "curl -s -H 'X-API-Key: your-api-key' \
              http://localhost:8080/api/v1/admin/import/jobs/$JOB_ID | jq '.progress'"

   # Check Redis progress keys
   redis-cli keys "*progress*"
   redis-cli get "import:progress:$JOB_ID"
   ```

2. **Review Progress Logging**:
   ```bash
   # Filter for progress-related logs
   docker logs honua-server | grep -E "(progress|%|imported.*features)"
   ```

**Solutions**:

1. **Reset Progress Tracking**:
   ```bash
   # Clear progress cache
   redis-cli del "import:progress:$JOB_ID"

   # Restart progress tracking
   curl -X POST -H "X-API-Key: your-api-key" \
        "http://localhost:8080/api/v1/admin/import/jobs/$JOB_ID/refresh-progress"
   ```

2. **Configure Progress Update Frequency**:
   ```bash
   # Set progress update intervals
   export Import__ProgressUpdateIntervalSeconds=5
   export Import__ProgressBatchSize=100
   ```

## Configuration and Environment Issues

### Issue: Import Service Not Starting

**Error**: Import endpoints return 503 Service Unavailable.

**Diagnostic Steps**:

1. **Check Service Registration**:
   ```bash
   # Verify import services in dependency injection
   docker logs honua-server | grep -E "(Import.*Service|Registration|DI)"

   # Test service health
   curl -s http://localhost:8080/health | jq '.components'
   ```

2. **Check Required Dependencies**:
   ```bash
   # Redis connectivity
   redis-cli ping

   # Database connectivity
   psql -h localhost -U postgres -d honua -c "SELECT 1;"

   # File storage permissions
   ls -la /var/lib/honua/uploads/
   ```

**Solutions**:

1. **Fix Service Configuration**:
   ```bash
   # Set required environment variables
   export Import__ServiceEnabled=true
   export Import__MaxConcurrentJobs=3
   export Import__TempFileDirectory="/tmp/honua-import"

   # Create required directories
   mkdir -p /var/lib/honua/uploads/temp
   chmod 755 /var/lib/honua/uploads/temp
   ```

2. **Verify Database Schema**:
   ```sql
   -- Check if import tables exist
   SELECT table_name FROM information_schema.tables
   WHERE table_schema = 'honua'
   AND table_name LIKE '%import%';

   -- Create missing tables if needed (run migrations)
   -- This is typically handled by DbUp migrations
   ```

## Data Transformation Issues

### Issue: Coordinate System Transformation Failures

**Error**: Features imported with incorrect coordinates.

**Solutions**:

1. **Verify Source Coordinate System**:
   ```bash
   # Check coordinate system of input data
   ogrinfo input.shp | grep -A 10 "Coordinate System"

   # Test coordinate transformation
   echo "POINT(-122.4194 37.7749)" | gdaltransform -s_srs EPSG:4326 -t_srs EPSG:3857
   ```

2. **Force Correct Source SRS**:
   ```bash
   # Override source SRS if incorrect
   ogr2ogr -s_srs EPSG:4326 -t_srs EPSG:4326 corrected.shp input.shp

   # Set target SRS for import
   export Import__TargetSpatialReference=4326
   ```

3. **Handle Unknown Coordinate Systems**:
   ```bash
   # Convert to known SRS before import
   ogr2ogr -f "ESRI Shapefile" -t_srs EPSG:4326 converted.shp unknown_srs.shp

   # Use well-known text (WKT) definition
   ogr2ogr -s_srs "+proj=utm +zone=33 +datum=WGS84" converted.shp input.shp
   ```

### Issue: Attribute Data Encoding Problems

**Error**: Special characters corrupted during import.

**Solutions**:

1. **Specify Character Encoding**:
   ```bash
   # Set encoding for shapefile import
   export Import__DefaultCharacterEncoding="UTF-8"

   # Override encoding per import
   ogr2ogr -lco ENCODING=UTF-8 output.shp input.shp
   ```

2. **Convert Encoding Before Import**:
   ```bash
   # Convert from Windows-1252 to UTF-8
   ogr2ogr -f "ESRI Shapefile" -lco ENCODING=UTF-8 utf8_output.shp cp1252_input.shp

   # Check encoding of DBF file
   file input.dbf
   ```

## Import Performance Optimization

### Slow Import Performance

**Solutions**:

1. **Optimize Database Settings**:
   ```sql
   -- Temporarily disable autovacuum during import
   ALTER TABLE honua.features SET (autovacuum_enabled = false);

   -- Increase work memory for import session
   SET work_mem = '256MB';

   -- Disable synchronous commit for bulk import
   SET synchronous_commit = off;
   ```

2. **Batch Insert Configuration**:
   ```bash
   # Configure batch sizes
   export Import__BatchSize=1000
   export Import__UseTransactions=true
   export Import__CommitInterval=5000
   ```

3. **Parallel Import Processing**:
   ```bash
   # Enable parallel import workers
   export Import__MaxConcurrentJobs=3
   export Import__ParallelProcessingEnabled=true
   ```

4. **Post-Import Optimization**:
   ```sql
   -- Re-enable autovacuum and analyze after import
   ALTER TABLE honua.features SET (autovacuum_enabled = true);
   ANALYZE honua.features;

   -- Update statistics
   VACUUM ANALYZE honua.features;
   ```

## Import Monitoring and Alerting

### Set Up Import Monitoring

1. **Monitor Import Queue**:
   ```bash
   #!/bin/bash
   # import-monitor.sh
   QUEUE_LENGTH=$(redis-cli llen import:queue)
   PROCESSING_COUNT=$(redis-cli llen import:processing)

   if [ $QUEUE_LENGTH -gt 10 ]; then
       echo "ALERT: Import queue backlog: $QUEUE_LENGTH jobs" | mail -s "Honua Import Alert" admin@example.com
   fi

   if [ $PROCESSING_COUNT -gt 5 ]; then
       echo "ALERT: Too many concurrent imports: $PROCESSING_COUNT" | mail -s "Honua Import Alert" admin@example.com
   fi
   ```

2. **Track Import Success Rates**:
   ```sql
   -- Import success rate query
   SELECT
       DATE(created_at) as import_date,
       COUNT(*) as total_imports,
       SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) as successful,
       ROUND(100.0 * SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) / COUNT(*), 2) as success_rate
   FROM honua.import_jobs
   WHERE created_at > NOW() - INTERVAL '7 days'
   GROUP BY DATE(created_at)
   ORDER BY import_date DESC;
   ```

## Getting Help

For import issues not covered here:

1. **Collect import diagnostics**:
   ```bash
   # Create import diagnostic report
   {
       echo "=== Import Service Status ==="
       curl -s http://localhost:8080/health | jq '.components.import'

       echo "=== Recent Import Jobs ==="
       curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
            http://localhost:8080/api/v1/admin/import/jobs?limit=10 | jq .

       echo "=== File Format Details ==="
       file problematic-file.*
       if [[ -f "problematic-file.zip" ]]; then
           unzip -l problematic-file.zip
       fi

       echo "=== System Resources ==="
       df -h
       free -h
       redis-cli info memory

       echo "=== Application Logs ==="
       docker logs honua-server 2>&1 | tail -100 | grep -E "(import|Import|ERROR)"
   } > import-diagnostic-report.txt
   ```

2. **Include sample data files that demonstrate the issue**
3. **Provide complete error messages and stack traces**
4. **Share file format details and data characteristics**
5. **Include system resource information and performance metrics**
