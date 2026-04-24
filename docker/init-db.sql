-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Generic PostGIS initialization for the currently selected POSTGRES_DB/POSTGRES_USER.
-- The Docker entrypoint connects as POSTGRES_USER to POSTGRES_DB before running this file.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
CREATE EXTENSION IF NOT EXISTS fuzzystrmatch;
CREATE EXTENSION IF NOT EXISTS postgis_tiger_geocoder;

CREATE SCHEMA IF NOT EXISTS honua;

DO $$
BEGIN
    EXECUTE format('GRANT USAGE, CREATE ON SCHEMA honua TO %I', current_user);
    EXECUTE format('GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA honua TO %I', current_user);
    EXECUTE format('GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA honua TO %I', current_user);
    EXECUTE format('GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA honua TO %I', current_user);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA honua GRANT ALL ON TABLES TO %I', current_user);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA honua GRANT ALL ON SEQUENCES TO %I', current_user);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA honua GRANT ALL ON FUNCTIONS TO %I', current_user);
    EXECUTE format(
        'ALTER ROLE %I IN DATABASE %I SET search_path = honua, public, topology',
        current_user,
        current_database());
END $$;

SELECT PostGIS_version();
