-- Migration 025: Create operational data schema
-- Ensures existing databases receive the same import/data-plane schema as fresh installs.

CREATE SCHEMA IF NOT EXISTS honua_data;

COMMENT ON SCHEMA honua_data IS 'Honua operational data tables imported or managed by operators';
