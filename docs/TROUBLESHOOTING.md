# Troubleshooting Guide

This document provides solutions to common issues when setting up and running Honua Server.

## Table of Contents

- [Database Connection Issues](#database-connection-issues)
- [Configuration Problems](#configuration-problems)
- [Performance Issues](#performance-issues)
- [API Errors](#api-errors)
- [Docker and Deployment Issues](#docker-and-deployment-issues)
- [Development Environment Setup](#development-environment-setup)

## Database Connection Issues

### Connection String Problems

**Symptom**: `InvalidOperationException: DefaultConnection connection string is required`

**Solution**:
1. Ensure the connection string is properly set:
   ```bash
   export ConnectionStrings__DefaultConnection="Host=localhost;Database=honua;Username=postgres;Password=postgres"
   ```

2. For Docker environments, verify the host points to the correct container:
   ```bash
   ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
   ```

**Symptom**: `Npgsql.NpgsqlException: Connection refused`

**Solution**:
1. Check PostgreSQL is running: `systemctl status postgresql` (Linux) or `brew services list | grep postgresql` (macOS)
2. Verify PostgreSQL is accepting connections on the correct port (default: 5432)
3. Check firewall settings are not blocking the connection
4. For Docker: Ensure containers are on the same network

### PostGIS Extension Missing

**Symptom**: `Npgsql.PostgresException: function st_geometrytype(geometry) does not exist`

**Solution**:
1. Install PostGIS extension in your database:
   ```sql
   CREATE EXTENSION IF NOT EXISTS postgis;
   CREATE EXTENSION IF NOT EXISTS postgis_topology;
   ```

2. Verify PostGIS is properly installed:
   ```sql
   SELECT PostGIS_Version();
   ```

## Configuration Problems

### Invalid Limits Configuration

**Symptom**: `InvalidOperationException: Invalid limits configuration`

**Solution**:
1. Check that numeric limits are within valid ranges:
   ```bash
   Limits__Query__MaxRecordCount=2000        # Must be 1-10000
   Limits__Query__DefaultRecordCount=1000    # Must be ≤ MaxRecordCount
   Limits__Geometry__MaxVertices=10000       # Must be 1000-1000000
   ```

2. Ensure timeout values are properly formatted:
   ```bash
   Limits__Query__QueryTimeout=00:00:30      # Format: HH:mm:ss
   Limits__Connections__RequestTimeout=00:01:00
   ```

### Missing Admin Password

**Symptom**: API returns 401 Unauthorized for admin endpoints

**Solution**:
```bash
export HONUA_ADMIN_PASSWORD="your-secure-password-here"
```

The password is used as an API key in the `X-API-Key` header for admin endpoints.

## Performance Issues

### Slow Query Performance

**Symptoms**:
- Query times exceeding 100ms for basic requests
- High memory usage during queries
- Database timeouts

**Solutions**:

1. **Check query limits**:
   ```bash
   # Reduce record counts for better performance
   Limits__Query__MaxRecordCount=1000
   Limits__Query__DefaultRecordCount=500
   ```

2. **Verify database indexes**:
   ```sql
   -- Check for spatial index on geometry column
   SELECT indexname, indexdef
   FROM pg_indexes
   WHERE tablename = 'features' AND indexdef LIKE '%gist%';
   ```

3. **Enable query optimization**:
   ```bash
   # Configure connection pooling
   Limits__Connections__MaxConcurrent=50
   ```

4. **Monitor query execution plans**:
   ```sql
   EXPLAIN ANALYZE SELECT * FROM honua.features WHERE layer_id = 1 LIMIT 100;
   ```

### Memory Usage Issues

**Symptoms**:
- Out of memory exceptions
- Gradual memory growth over time

**Solutions**:

1. **Reduce geometry complexity limits**:
   ```bash
   Limits__Geometry__MaxVertices=5000
   Limits__Geometry__MaxPolygons=50
   ```

2. **Configure garbage collection** (for .NET applications):
   ```bash
   DOTNET_GCConserveMemory=3
   DOTNET_GCHeapHardLimit=1073741824  # 1GB limit
   ```

## API Errors

### 400 Bad Request Errors

**Symptom**: `Invalid query syntax: WHERE clause format not supported`

**Solution**:
- Use simple comparison operators: `=`, `!=`, `>`, `<`, `>=`, `<=`
- Ensure string values are properly quoted: `name = 'Test Feature'`
- For CQL2 filters, verify syntax: `name LIKE 'Test%' AND category = 'active'`

**Symptom**: `Invalid geometry format`

**Solution**:
1. For GeoServices REST, use proper JSON geometry format:
   ```json
   {
     "geometry": {
       "x": -122.4194,
       "y": 37.7749
     },
     "geometryType": "esriGeometryPoint",
     "spatialRel": "esriSpatialRelIntersects"
   }
   ```

2. For OGC API Features, use WKT or GeoJSON:
   ```
   POINT(-122.4194 37.7749)
   ```

### 500 Internal Server Error

**Symptom**: Server errors during feature queries or edits

**Solution**:
1. Check application logs for detailed error messages
2. Verify database schema is properly initialized:
   ```sql
   SELECT * FROM information_schema.tables WHERE table_schema = 'honua';
   ```
3. Ensure proper permissions on database objects

## Docker and Deployment Issues

### Container Startup Issues

**Symptom**: Container exits immediately or fails health checks

**Solution**:
1. Check logs: `docker logs honua-server`
2. Verify environment variables are properly set
3. Ensure database container is healthy before starting app container

**Example docker-compose.yml**:
```yaml
version: '3.8'
services:
  postgres:
    image: postgis/postgis:15-3.4
    environment:
      POSTGRES_DB: honua
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: postgres
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 10s
      timeout: 5s
      retries: 5

  honua:
    image: honua-server:latest
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=postgres;Password=postgres"
      HONUA_ADMIN_PASSWORD: "secure-password"
```

### Network Connectivity Issues

**Symptom**: Cannot access API endpoints from outside container

**Solution**:
1. Ensure ports are properly exposed: `docker run -p 8080:8080 honua-server`
2. Check container is listening on all interfaces (0.0.0.0), not just localhost
3. Verify firewall rules allow traffic on the specified port

## Development Environment Setup

### Build Errors

**Symptom**: `CS0246: The type or namespace name could not be found`

**Solution**:
1. Restore NuGet packages: `dotnet restore`
2. Clean and rebuild: `dotnet clean && dotnet build`
3. Check .NET SDK version: `dotnet --version` (requires .NET 8.0 or later)

**Symptom**: Test failures due to database issues

**Solution**:
1. Ensure PostgreSQL with PostGIS is running for integration tests
2. Check test database permissions
3. Run tests with proper environment: `dotnet test --logger "console;verbosity=detailed"`

### IDE Issues

**Symptoms**:
- IntelliSense not working
- Build errors in IDE but command line works

**Solution**:
1. Reload IDE window/restart IDE
2. Clear IDE caches
3. Ensure IDE is using correct .NET SDK version
4. For VS Code: Install C# extension and reload window

## Getting Help

If these troubleshooting steps don't resolve your issue:

1. **Check the logs**: Look for detailed error messages in application logs
2. **Review configuration**: Verify all required environment variables are set correctly
3. **Test database connectivity**: Use `psql` or another PostgreSQL client to verify database access
4. **Create minimal reproduction**: Try to reproduce the issue with minimal configuration
5. **Check GitHub Issues**: Look for similar issues in the project repository

For additional support, please file an issue with:
- Detailed error messages and stack traces
- Configuration (with sensitive information redacted)
- Steps to reproduce the problem
- Environment details (OS, .NET version, PostgreSQL version)