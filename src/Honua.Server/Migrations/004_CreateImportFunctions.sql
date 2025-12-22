-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Import helper functions to keep application SQL text static while safely handling identifiers.

CREATE SCHEMA IF NOT EXISTS honua;

CREATE OR REPLACE FUNCTION honua.create_import_table(table_name text)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF table_name IS NULL OR length(trim(table_name)) = 0 THEN
        RAISE EXCEPTION 'Table name cannot be null or empty';
    END IF;

    IF length(table_name) > 63 THEN
        RAISE EXCEPTION 'Table name exceeds PostgreSQL identifier limit of 63 characters';
    END IF;

    IF table_name !~ '^[a-zA-Z][a-zA-Z0-9_]*$' THEN
        RAISE EXCEPTION 'Table name must start with a letter and contain only letters, digits, and underscores';
    END IF;

    EXECUTE format('DROP TABLE IF EXISTS %I', table_name);
    EXECUTE format(
        'CREATE TABLE %I (id SERIAL PRIMARY KEY, geometry GEOMETRY, properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        table_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I USING GIST (geometry)', 'idx_' || table_name || '_geometry', table_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I USING GIN (properties)', 'idx_' || table_name || '_properties', table_name);
END;
$$;

CREATE OR REPLACE FUNCTION honua.insert_import_feature(
    table_name text,
    wkb bytea,
    source_srid integer,
    target_srid integer,
    properties jsonb)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    EXECUTE format(
        'INSERT INTO %I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4)',
        table_name)
    USING wkb, source_srid, target_srid, properties;
END;
$$;
