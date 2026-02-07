# Honua Database Connection Troubleshooting

Troubleshoot Honua Server database connection issues in containerized deployments.

## Quick Diagnosis

```bash
# Check Honua container logs
docker logs honua-server 2>&1 | grep -i "connection\|database\|timeout"

# Test Honua database connection
docker exec honua-server psql $ConnectionStrings__DefaultConnection -c "SELECT version();"

# Check Honua schema
docker exec postgres psql -U honua -d honua -c "SELECT schemaname FROM pg_tables WHERE schemaname = 'honua';"

# Verify Honua migrations
curl http://localhost:8080/api/v1/admin/database/health
```

## Connection String Issues

### Issue: `InvalidOperationException: DefaultConnection connection string is required`

**Root Cause**: The application cannot find or parse the database connection string.

**Solutions**:

1. **Environment Variable (Local Development)**:
   ```bash
   export ConnectionStrings__DefaultConnection="Host=localhost;Database=honua;Username=postgres;Password=postgres;Port=5432"
   ```

2. **Docker Compose**:
   ```yaml
   services:
     honua-server:
       environment:
         ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=postgres;Password=postgres"
       depends_on:
         postgres:
           condition: service_healthy
   ```

3. **Kubernetes**:
   ```yaml
   env:
   - name: ConnectionStrings__DefaultConnection
     valueFrom:
       secretKeyRef:
         name: honua-db-secret
         key: connection-string
   ```

**Verification**:
```bash
# Check environment variables
env | grep ConnectionStrings

# Test connection manually
dotnet run --project src/Honua.Server --environment Development
```

### Issue: `Npgsql.NpgsqlException: Connection refused`

**Root Cause**: Honua can't reach the PostgreSQL container/service.

**Solutions**:

1. **Check Docker Compose services**:
   ```bash
   # Verify postgres container is running
   docker ps | grep postgres

   # Check postgres container health
   docker exec postgres pg_isready -U honua

   # Check network connectivity between containers
   docker exec honua-server ping postgres
   ```

2. **Verify connection string**:
   ```bash
   # Check environment variables in container
   docker exec honua-server env | grep ConnectionStrings

   # Should be: ConnectionStrings__DefaultConnection=Host=postgres;Database=honua;Username=honua;Password=...
   ```

3. **Check Docker Compose configuration**:
   ```yaml
   services:
     honua-server:
       depends_on:
         postgres:
           condition: service_healthy
     postgres:
       healthcheck:
         test: ["CMD-SHELL", "pg_isready -U honua"]
         interval: 10s
         timeout: 5s
         retries: 5
   ```

### Issue: `Npgsql.PostgresException: FATAL: database "honua" does not exist`

**Root Cause**: Database not created in PostgreSQL container.

**Solution**:
```bash
# Check PostgreSQL environment variables
docker exec postgres env | grep POSTGRES

# Database should be auto-created with POSTGRES_DB=honua
# Verify in Docker Compose:
services:
  postgres:
    environment:
      POSTGRES_DB: honua  # Creates database on startup
```

### Issue: `Npgsql.PostgresException: FATAL: password authentication failed`

**Root Cause**: Incorrect Honua database credentials.

**Solutions**:

1. **Verify Docker Compose credentials**:
   ```yaml
   services:
     postgres:
       environment:
         POSTGRES_DB: honua
         POSTGRES_USER: honua
         POSTGRES_PASSWORD: yourpassword
     honua-server:
       environment:
         ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=honua;Password=yourpassword"
   ```

2. **Check Kubernetes secrets**:
   ```bash
   # Verify secret exists
   kubectl get secret honua-db-secret

   # Check secret values
   kubectl get secret honua-db-secret -o yaml
   ```

3. **Test credentials manually**:
   ```bash
   # Test from Honua container
   docker exec honua-server psql "Host=postgres;Database=honua;Username=honua;Password=yourpassword" -c "SELECT 1;"
   ```

## PostGIS Extension Issues

### Issue: `Npgsql.PostgresException: function st_geometrytype(geometry) does not exist`

**Root Cause**: PostGIS extension is not enabled in the Honua database.

**Solution**:

1. **Use PostGIS container image**:
   ```yaml
   services:
     postgres:
       image: postgis/postgis:16-3.4
       environment:
         POSTGRES_DB: honua
         POSTGRES_USER: honua
         POSTGRES_PASSWORD: yourpassword
   ```

2. **Verify PostGIS is available**:
   ```bash
   # Check PostGIS version in container
   docker exec postgres psql -U honua -d honua -c "SELECT PostGIS_Version();"

   # If PostGIS not enabled, enable it
   docker exec postgres psql -U honua -d honua -c "CREATE EXTENSION IF NOT EXISTS postgis;"
   ```

3. **Check Honua migration status**:
   ```bash
   # Honua automatically enables PostGIS during migrations
   curl http://localhost:8080/api/v1/admin/database/health

   # Check migration logs
   docker logs honua-server | grep -i "postgis\|migration"
   ```

## Schema and Migration Issues

### Issue: `Npgsql.PostgresException: relation "honua.layers" does not exist`

**Root Cause**: Database schema hasn't been initialized or migrations haven't been run.

**Solution**:

1. **Check Honua migration status**:
   ```bash
   # Honua auto-migrates on startup
   docker logs honua-server | grep -i "migration\|schema"

   # Check database health endpoint
   curl http://localhost:8080/api/v1/admin/database/health
   ```

2. **Force migration via Admin API**:
   ```bash
   # Trigger migration via API
   curl -X POST http://localhost:8080/api/v1/admin/database/migrate

   # Check migration logs
   docker logs honua-server -f
   ```

3. **Verify Honua schema**:
   ```bash
   # Check Honua tables exist
   docker exec postgres psql -U honua -d honua -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'honua';"

   # Should show: layers, features, connections, etc.
   ```

## Docker-Specific Issues

### Issue: Container startup failures with database connections

**Root Cause**: Timing issues between database and application containers.

**Solution**:

1. **Proper Docker Compose Setup**:
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
         test: ["CMD-SHELL", "pg_isready -U postgres -d honua"]
         interval: 10s
         timeout: 5s
         retries: 5
       volumes:
         - postgres_data:/var/lib/postgresql/data

     honua-server:
       build: .
       depends_on:
         postgres:
           condition: service_healthy  # Wait for postgres to be healthy
       environment:
         ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=postgres;Password=postgres"
         ASPNETCORE_ENVIRONMENT: Development
       ports:
         - "8080:8080"

   volumes:
     postgres_data:
   ```

2. **Application-Level Retry Logic**:
   ```csharp
   // Already implemented in Honua.Server startup
   // Retries database connection with exponential backoff
   ```

3. **Debugging Container Issues**:
   ```bash
   # Check container logs
   docker logs honua-postgres
   docker logs honua-server

   # Test network connectivity between containers
   docker exec honua-server ping postgres

   # Connect to database from application container
   docker exec -it honua-server psql -h postgres -U postgres -d honua
   ```

## Honua Performance Issues

### Issue: `TimeoutException: Timeout expired`

**Root Cause**: Honua queries timing out or connection pool exhausted.

**Solutions**:

1. **Check Honua performance metrics**:
   ```bash
   # Check Honua performance endpoint
   curl http://localhost:8080/api/v1/admin/performance/summary

   # Check slow query logs
   docker logs honua-server | grep -i "slow\|timeout\|performance"
   ```

2. **Tune Honua connection pool**:
   ```bash
   # Increase connection pool size
   HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE=200
   HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT=200
   HONUA__LIMITS__CONNECTIONS__REQUESTTIMEOUT=60s
   ```

3. **Check PostgreSQL container resources**:
   ```bash
   # Monitor container resource usage
   docker stats postgres

   # Check if container is resource-constrained
   docker exec postgres cat /proc/meminfo | grep MemAvailable
   ```

## Collect Honua Diagnostics

If issues persist, collect this information:

```bash
# Honua container diagnostics
docker logs honua-server > honua-diagnostics.log 2>&1
docker inspect honua-server > honua-container-info.json

# PostgreSQL container diagnostics
docker logs postgres > postgres-diagnostics.log 2>&1
docker exec postgres psql -U honua -d honua -c "SELECT version();" >> postgres-info.txt

# Network connectivity test
docker exec honua-server ping postgres >> network-test.txt

# Honua health check
curl http://localhost:8080/healthz/ready >> health-check.txt
curl http://localhost:8080/api/v1/admin/database/health >> db-health.txt

# Environment configuration
docker exec honua-server env | grep -i "connection\|honua" >> env-vars.txt
```

**Submit diagnostics with**:
- Complete error messages from logs
- Docker Compose / Kubernetes configuration (redact passwords)
- Steps to reproduce the issue
