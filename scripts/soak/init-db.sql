-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- PostGIS provisioning for the capacity-soak substrate. The Production startup policy runs
-- a PostGIS preflight check (engine + postgis + postgis_raster) before migrations, and it
-- fails closed, so these extensions must exist before the server first connects. The Docker
-- entrypoint runs this as POSTGRES_USER against POSTGRES_DB.
--
-- Nothing here creates a migration-owned table: the server owns the core schema and creates
-- it through its own migrations (honua-server#3812). Creating those tables here would leave
-- them present with no journal row and PostgresCoreSchemaGuard would refuse to start.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_raster;
CREATE EXTENSION IF NOT EXISTS unaccent;
