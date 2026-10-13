-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- honua:compatibility-review reason=Refuse destructive import replacement of unowned tables; legacy imports require explicit database-owner adoption.
-- Never infer ownership from a catalog layer: existing customer tables can be published too.
CREATE OR REPLACE FUNCTION honua.assert_import_table_owned(schema_name text, table_name text)
RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');
    IF to_regclass(format('%I.%I', schema_name, table_name)) IS NULL THEN
        RETURN false;
    END IF;
    EXECUTE format('LOCK TABLE %I.%I IN EXCLUSIVE MODE', schema_name, table_name);
    IF obj_description(to_regclass(format('%I.%I', schema_name, table_name)), 'pg_class')
        IS DISTINCT FROM 'honua:import-owned:v1' THEN
        RAISE EXCEPTION 'IMPORT_TARGET_NOT_OWNED: Table %.% is not owned by the importer', schema_name, table_name
            USING ERRCODE = 'HN409';
    END IF;
    RETURN true;
END;
$$;

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
    PERFORM honua.assert_import_table_owned(schema_name, table_name);
    IF honua.assert_import_table_owned(schema_name, table_name) THEN
        EXECUTE format('DROP TABLE %I.%I', schema_name, table_name);
    END IF;
    EXECUTE format(
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, table_name, target_srid);
    EXECUTE format('COMMENT ON TABLE %I.%I IS %L', schema_name, table_name, 'honua:import-owned:v1');
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
    IF to_regclass(format('%I.%I', schema_name, table_name)) IS NOT NULL THEN
        RETURN;
    END IF;
    EXECUTE format(
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, table_name, target_srid);
    EXECUTE format('COMMENT ON TABLE %I.%I IS %L', schema_name, table_name, 'honua:import-owned:v1');
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
    PERFORM honua.assert_import_table_owned(schema_name, table_name);
    PERFORM honua.assert_import_table_owned(schema_name, staging_name);
    IF honua.assert_import_table_owned(schema_name, staging_name) THEN
        EXECUTE format('DROP TABLE %I.%I', schema_name, staging_name);
    END IF;
    EXECUTE format(
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, staging_name, target_srid);
    EXECUTE format('COMMENT ON TABLE %I.%I IS %L', schema_name, staging_name, 'honua:import-owned:v1');
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || staging_name || '_geometry', schema_name, staging_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || staging_name || '_properties', schema_name, staging_name);

    RETURN staging_name;
END;
$$;

CREATE OR REPLACE FUNCTION honua.swap_import_table(
    schema_name text,
    table_name text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    staging_name text;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');

    staging_name := honua.import_staging_table_name(table_name);

    PERFORM honua.assert_import_table_owned(schema_name, table_name);
    IF NOT honua.assert_import_table_owned(schema_name, staging_name) THEN
        RAISE EXCEPTION 'IMPORT_TARGET_NOT_OWNED: Required staging table %.% is absent', schema_name, staging_name
            USING ERRCODE = 'HN409';
    END IF;
    IF honua.assert_import_table_owned(schema_name, table_name) THEN
        EXECUTE format('DROP TABLE %I.%I CASCADE', schema_name, table_name);
    END IF;
    EXECUTE format('ALTER TABLE %I.%I RENAME TO %I', schema_name, staging_name, table_name);

    -- Rename the staging indexes onto the canonical names so a later load (which
    -- references idx_<table>_geometry / idx_<table>_properties) does not collide.
    -- PostgreSQL truncates these derived names to 63 chars exactly as the live
    -- create path relies on.
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_geometry', 'idx_' || table_name || '_geometry');
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_properties', 'idx_' || table_name || '_properties');
END;
$$;

CREATE OR REPLACE FUNCTION honua.drop_import_staging_table(schema_name text, table_name text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    staging_name text;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');
    staging_name := honua.import_staging_table_name(table_name);

    PERFORM honua.assert_import_table_owned(schema_name, staging_name);
    IF honua.assert_import_table_owned(schema_name, staging_name) THEN
        EXECUTE format('DROP TABLE %I.%I', schema_name, staging_name);
    END IF;
END;
$$;
