-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Import helper functions to keep application SQL text static while safely handling identifiers.

CREATE SCHEMA IF NOT EXISTS honua;
CREATE SCHEMA IF NOT EXISTS honua_data;

CREATE OR REPLACE FUNCTION honua.create_import_table(schema_name text, table_name text, target_srid integer DEFAULT 4326)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF schema_name IS NULL OR length(trim(schema_name)) = 0 THEN
        RAISE EXCEPTION 'Schema name cannot be null or empty';
    END IF;

    IF schema_name !~ '^[a-zA-Z_][a-zA-Z0-9_]*$' THEN
        RAISE EXCEPTION 'Schema name must start with a letter or underscore and contain only letters, digits, and underscores';
    END IF;

    IF table_name IS NULL OR length(trim(table_name)) = 0 THEN
        RAISE EXCEPTION 'Table name cannot be null or empty';
    END IF;

    IF length(table_name) > 63 THEN
        RAISE EXCEPTION 'Table name exceeds PostgreSQL identifier limit of 63 characters';
    END IF;

    IF table_name !~ '^[a-zA-Z][a-zA-Z0-9_]*$' THEN
        RAISE EXCEPTION 'Table name must start with a letter and contain only letters, digits, and underscores';
    END IF;

    IF target_srid IS NULL OR target_srid <= 0 THEN
        RAISE EXCEPTION 'Target SRID must be a positive integer';
    END IF;

    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
    EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, table_name);
    EXECUTE format(
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, table_name, target_srid);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || table_name || '_geometry', schema_name, table_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || table_name || '_properties', schema_name, table_name);
END;
$$;

CREATE OR REPLACE FUNCTION honua.create_import_table(table_name text)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM honua.create_import_table('honua_data', table_name, 4326);
END;
$$;

CREATE OR REPLACE FUNCTION honua.insert_import_feature(
    schema_name text,
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
        'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4)',
        schema_name, table_name)
    USING wkb, source_srid, target_srid, properties;
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
    PERFORM honua.insert_import_feature('honua_data', table_name, wkb, source_srid, target_srid, properties);
END;
$$;
