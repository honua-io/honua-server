-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Initialize PostGIS extensions and create development schema
\c honua_dev;

-- Enable PostGIS extensions
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
CREATE EXTENSION IF NOT EXISTS fuzzystrmatch;
CREATE EXTENSION IF NOT EXISTS postgis_tiger_geocoder;

-- Create schema for application data
CREATE SCHEMA IF NOT EXISTS honua;

-- Grant permissions to honua user
GRANT ALL PRIVILEGES ON SCHEMA honua TO honua;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA honua TO honua;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA honua TO honua;
GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA honua TO honua;

-- Set default privileges for future objects
ALTER DEFAULT PRIVILEGES IN SCHEMA honua GRANT ALL ON TABLES TO honua;
ALTER DEFAULT PRIVILEGES IN SCHEMA honua GRANT ALL ON SEQUENCES TO honua;
ALTER DEFAULT PRIVILEGES IN SCHEMA honua GRANT ALL ON FUNCTIONS TO honua;

-- Create test schema for integration tests
CREATE SCHEMA IF NOT EXISTS honua_test;
GRANT ALL PRIVILEGES ON SCHEMA honua_test TO honua;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA honua_test TO honua;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA honua_test TO honua;
GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA honua_test TO honua;

ALTER DEFAULT PRIVILEGES IN SCHEMA honua_test GRANT ALL ON TABLES TO honua;
ALTER DEFAULT PRIVILEGES IN SCHEMA honua_test GRANT ALL ON SEQUENCES TO honua;
ALTER DEFAULT PRIVILEGES IN SCHEMA honua_test GRANT ALL ON FUNCTIONS TO honua;

-- Verify PostGIS installation
SELECT PostGIS_version();