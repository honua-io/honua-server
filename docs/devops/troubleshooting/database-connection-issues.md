# Database Connection Troubleshooting Guide

This guide provides comprehensive solutions for PostgreSQL and PostGIS connection issues in Honua Server.

## Quick Diagnosis

Run the following commands to quickly identify the issue:

```bash
# Check if PostgreSQL is running
systemctl status postgresql  # Linux
brew services list | grep postgresql  # macOS

# Test basic connectivity
psql -h localhost -U postgres -d honua -c "SELECT version();"

# Verify PostGIS installation
psql -h localhost -U postgres -d honua -c "SELECT PostGIS_Version();"

# Check Honua schema
psql -h localhost -U postgres -d honua -c "SELECT schemaname FROM pg_tables WHERE schemaname = 'honua';"
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

### Issue: `Npgsql.NpgsqlException: Connection refused (0x0000274D/10061)`

**Root Cause**: PostgreSQL server is not running or not accepting connections.

**Diagnostic Steps**:

1. **Verify PostgreSQL Status**:
   ```bash
   # Linux systems
   sudo systemctl status postgresql

   # macOS with Homebrew
   brew services list | grep postgresql

   # Check specific version
   sudo systemctl status postgresql@14-main  # Replace with your version
   ```

2. **Check PostgreSQL Configuration**:
   ```bash
   # Find configuration files
   sudo find /etc/postgresql -name "postgresql.conf" -type f 2>/dev/null

   # Check if PostgreSQL is listening on correct addresses
   sudo grep "^listen_addresses" /etc/postgresql/*/main/postgresql.conf

   # Should show: listen_addresses = '*' or 'localhost'
   ```

3. **Verify Port Availability**:
   ```bash
   # Check if PostgreSQL is listening on port 5432
   netstat -tlnp | grep :5432

   # Alternative using ss
   ss -tlnp | grep :5432

   # Test connectivity to port
   telnet localhost 5432
   ```

**Solutions**:

1. **Start PostgreSQL Service**:
   ```bash
   # Linux systems
   sudo systemctl start postgresql
   sudo systemctl enable postgresql  # Auto-start on boot

   # macOS with Homebrew
   brew services start postgresql
   ```

2. **Configure PostgreSQL for External Connections**:
   ```bash
   # Edit postgresql.conf
   sudo nano /etc/postgresql/14/main/postgresql.conf

   # Change:
   listen_addresses = 'localhost'  # or '*' for all interfaces
   port = 5432

   # Edit pg_hba.conf for authentication
   sudo nano /etc/postgresql/14/main/pg_hba.conf

   # Add line for local connections:
   host    all             all             127.0.0.1/32            md5
   ```

3. **Restart PostgreSQL**:
   ```bash
   sudo systemctl restart postgresql
   ```

### Issue: `Npgsql.PostgresException: FATAL: database "honua" does not exist`

**Root Cause**: The target database hasn't been created.

**Solution**:
```bash
# Create database
createdb -h localhost -U postgres honua

# Alternative using psql
psql -h localhost -U postgres -c "CREATE DATABASE honua;"

# Verify database creation
psql -h localhost -U postgres -l | grep honua
```

### Issue: `Npgsql.PostgresException: FATAL: password authentication failed`

**Root Cause**: Incorrect username or password.

**Solutions**:

1. **Reset PostgreSQL Password**:
   ```bash
   # Switch to postgres user
   sudo -u postgres psql

   # Reset password
   ALTER USER postgres PASSWORD 'newpassword';
   \q
   ```

2. **Check Authentication Method**:
   ```bash
   # Check pg_hba.conf
   sudo cat /etc/postgresql/14/main/pg_hba.conf | grep -v '^#'

   # For development, you might use 'trust' method:
   local   all             postgres                                trust
   host    all             all             127.0.0.1/32            trust
   ```

3. **Update Connection String**:
   ```bash
   export ConnectionStrings__DefaultConnection="Host=localhost;Database=honua;Username=postgres;Password=newpassword"
   ```

## PostGIS Extension Issues

### Issue: `Npgsql.PostgresException: function st_geometrytype(geometry) does not exist`

**Root Cause**: PostGIS extension is not installed or enabled.

**Solution**:

1. **Install PostGIS**:
   ```bash
   # Ubuntu/Debian
   sudo apt-get install postgresql-14-postgis-3

   # CentOS/RHEL
   sudo yum install postgis33_14

   # macOS with Homebrew
   brew install postgis
   ```

2. **Enable PostGIS in Database**:
   ```sql
   -- Connect to honua database
   psql -h localhost -U postgres -d honua

   -- Create PostGIS extension
   CREATE EXTENSION IF NOT EXISTS postgis;
   CREATE EXTENSION IF NOT EXISTS postgis_topology;

   -- Verify installation
   SELECT PostGIS_Version();
   SELECT PostGIS_Full_Version();

   -- Check available spatial reference systems
   SELECT count(*) FROM spatial_ref_sys;
   ```

3. **Verify PostGIS Functions**:
   ```sql
   -- Test basic PostGIS functionality
   SELECT ST_Point(-122.4194, 37.7749) as point;
   SELECT ST_GeometryType(ST_Point(-122.4194, 37.7749));
   ```

### Issue: `Npgsql.PostgresException: permission denied for schema topology`

**Root Cause**: Database user lacks permissions for PostGIS topology functions.

**Solution**:
```sql
-- Connect as postgres superuser
psql -h localhost -U postgres -d honua

-- Grant permissions
GRANT USAGE ON SCHEMA topology TO your_app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA topology TO your_app_user;

-- For new tables
ALTER DEFAULT PRIVILEGES IN SCHEMA topology GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO your_app_user;
```

## Schema and Migration Issues

### Issue: `Npgsql.PostgresException: relation "honua.layers" does not exist`

**Root Cause**: Database schema hasn't been initialized or migrations haven't been run.

**Solution**:

1. **Run Database Migrations**:
   ```bash
   # From project root
   dotnet run --project src/Honua.Server -- --migrate

   # Check migration status
   psql -h localhost -U postgres -d honua -c "SELECT * FROM honua.schema_versions ORDER BY applied_on;"
   ```

2. **Verify Schema Structure**:
   ```sql
   -- Check if honua schema exists
   SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'honua';

   -- List tables in honua schema
   SELECT table_name FROM information_schema.tables WHERE table_schema = 'honua';

   -- Check table structure
   \d honua.layers
   \d honua.features
   ```

3. **Manual Schema Creation** (if automated migration fails):
   ```sql
   -- Create schema
   CREATE SCHEMA IF NOT EXISTS honua;

   -- Set search path
   SET search_path TO honua, public;

   -- Create basic tables (refer to migration scripts for complete schema)
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

## Performance and Connection Pool Issues

### Issue: `TimeoutException: Timeout expired. The timeout period elapsed prior to completion`

**Root Cause**: Database queries taking too long or connection pool exhaustion.

**Diagnostic Steps**:
```sql
-- Check active connections
SELECT count(*) FROM pg_stat_activity WHERE state = 'active';

-- Check long-running queries
SELECT pid, now() - pg_stat_activity.query_start AS duration, query
FROM pg_stat_activity
WHERE (now() - pg_stat_activity.query_start) > interval '5 minutes';

-- Check for locks
SELECT
    blocked_locks.pid AS blocked_pid,
    blocked_activity.usename AS blocked_user,
    blocking_locks.pid AS blocking_pid,
    blocking_activity.usename AS blocking_user,
    blocked_activity.query AS blocked_statement,
    blocking_activity.query AS current_statement_in_blocking_process
FROM pg_catalog.pg_locks blocked_locks
JOIN pg_catalog.pg_stat_activity blocked_activity ON blocked_activity.pid = blocked_locks.pid
JOIN pg_catalog.pg_locks blocking_locks
    ON blocking_locks.locktype = blocked_locks.locktype
    AND blocking_locks.DATABASE IS NOT DISTINCT FROM blocked_locks.DATABASE
JOIN pg_catalog.pg_stat_activity blocking_activity ON blocking_activity.pid = blocking_locks.pid
WHERE NOT blocked_locks.GRANTED;
```

**Solutions**:

1. **Optimize Connection String**:
   ```bash
   export ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres;Maximum Pool Size=50;Connection Idle Lifetime=300"
   ```

2. **Query Optimization**:
   ```sql
   -- Add spatial indexes if missing
   CREATE INDEX CONCURRENTLY idx_features_geom ON honua.features USING gist(geometry);
   CREATE INDEX CONCURRENTLY idx_features_layer_id ON honua.features(layer_id);

   -- Update table statistics
   ANALYZE honua.features;
   ANALYZE honua.layers;
   ```

3. **Monitor Query Performance**:
   ```sql
   -- Enable query logging (postgresql.conf)
   log_min_duration_statement = 1000  # Log queries taking > 1 second
   log_statement = 'all'              # Log all statements (development only)

   -- Or check pg_stat_statements
   SELECT query, calls, total_time, mean_time
   FROM pg_stat_statements
   ORDER BY total_time DESC
   LIMIT 10;
   ```

## Advanced Troubleshooting

### Memory and Resource Issues

```bash
# Check PostgreSQL memory usage
ps aux | grep postgres

# Check disk space
df -h
du -sh /var/lib/postgresql/

# PostgreSQL memory configuration (postgresql.conf)
shared_buffers = 256MB          # 25% of available RAM
effective_cache_size = 1GB      # 75% of available RAM
work_mem = 4MB                  # Per connection
maintenance_work_mem = 64MB     # For maintenance operations
```

### SSL/TLS Issues

```bash
# Test SSL connection
psql "host=localhost user=postgres dbname=honua sslmode=require"

# Check SSL configuration
sudo grep ssl /etc/postgresql/14/main/postgresql.conf

# Generate self-signed certificate for testing
sudo openssl req -new -x509 -days 365 -nodes -text -out server.crt -keyout server.key -subj "/CN=localhost"
sudo chown postgres:postgres server.crt server.key
```

### Network and Firewall Issues

```bash
# Check firewall status
sudo ufw status verbose  # Ubuntu
sudo firewall-cmd --list-all  # CentOS/RHEL

# Open PostgreSQL port
sudo ufw allow 5432/tcp
sudo firewall-cmd --add-port=5432/tcp --permanent

# Test network connectivity
nmap -p 5432 localhost
nc -v localhost 5432
```

## Getting Help

If these solutions don't resolve your issue:

1. **Collect diagnostic information**:
   ```bash
   # System information
   uname -a
   docker --version
   dotnet --version

   # PostgreSQL version and configuration
   psql --version
   psql -h localhost -U postgres -c "SHOW server_version;"
   psql -h localhost -U postgres -c "SHOW config_file;"

   # Application logs
   docker logs honua-server > honua-server.log 2>&1
   ```

2. **Check the application logs for specific error messages**
3. **Verify your configuration matches the examples in this guide**
4. **Test with minimal configuration to isolate the issue**
5. **Check GitHub Issues for similar problems**

For additional support, create an issue with:
- Complete error messages and stack traces
- Configuration (with sensitive data redacted)
- Steps to reproduce
- Environment details (OS, PostgreSQL version, Docker version)
