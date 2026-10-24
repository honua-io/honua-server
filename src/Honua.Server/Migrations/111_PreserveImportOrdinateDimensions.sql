-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- honua:compatibility-review reason=Existing import replacement semantics remain unchanged; new tables accept source ordinate dimensions while retaining SRID validation.

-- A Geometry typmod defaults to XY and rejects valid GPX elevations. Keep the
-- target SRID constraint while allowing XY/XYZ/XYM/XYZM rows without coercion.
-- Existing append/upsert targets retain their declared schema and constraints.

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
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, table_name, target_srid);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || table_name || '_geometry', schema_name, table_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || table_name || '_properties', schema_name, table_name);
END;
$$;

CREATE OR REPLACE FUNCTION honua.ensure_import_table(
    schema_name text,
    table_name text,
    target_srid integer DEFAULT 4326)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');

    IF target_srid IS NULL OR target_srid <= 0 THEN
        RAISE EXCEPTION 'Target SRID must be a positive integer';
    END IF;

    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, table_name, target_srid);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || table_name || '_geometry', schema_name, table_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || table_name || '_properties', schema_name, table_name);
END;
$$;

CREATE OR REPLACE FUNCTION honua.create_import_staging_table(
    schema_name text,
    table_name text,
    target_srid integer DEFAULT 4326)
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    staging_name text;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');

    IF target_srid IS NULL OR target_srid <= 0 THEN
        RAISE EXCEPTION 'Target SRID must be a positive integer';
    END IF;

    staging_name := honua.import_staging_table_name(table_name);

    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
    -- A leftover staging table from a previously-crashed load is safe to drop: it
    -- is never the live target and only ever holds in-flight, not-yet-swapped rows.
    EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, staging_name);
    EXECUTE format(
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, staging_name, target_srid);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || staging_name || '_geometry', schema_name, staging_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || staging_name || '_properties', schema_name, staging_name);

    RETURN staging_name;
END;
$$;
