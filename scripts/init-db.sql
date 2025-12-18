-- PostgreSQL initialization script for Honua development
-- Creates PostGIS extension and basic database structure

-- Enable PostGIS extension
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;

-- Create schema for Honua tables
CREATE SCHEMA IF NOT EXISTS honua;

-- Grant permissions to honua_user
GRANT USAGE ON SCHEMA honua TO honua_user;
GRANT CREATE ON SCHEMA honua TO honua_user;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA honua TO honua_user;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA honua TO honua_user;

-- Set default schema search path
ALTER USER honua_user SET search_path = honua, public, topology;

-- Display PostGIS version for verification
SELECT PostGIS_Version();