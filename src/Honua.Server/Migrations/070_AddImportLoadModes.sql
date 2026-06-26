-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Import load-mode helpers (#ETL-load-modes). The original import path was
-- full-replace only: honua.create_import_table unconditionally DROP+CREATEs and
-- honua.insert_import_feature does a plain INSERT, so there was no way to add to
-- or incrementally update an existing target. This migration adds the SQL needed
-- for the three load modes the application now exposes:
--
--   * replace  — transactional staging-table-swap so a failed load never leaves a
--                half-dropped target (honua.create_import_staging_table builds an
--                empty sibling, the load streams into it, honua.swap_import_table
--                atomically renames it over the live table inside one transaction).
--   * append   — plain INSERT into the existing table (reuses insert_import_feature).
--   * upsert   — keyed ON CONFLICT (key) DO UPDATE through a stable unique index
--                over one or more property keys.
--
-- All DDL stays inside plpgsql function bodies so application SQL text remains
-- static and identifiers are handled with format(%I) rather than interpolation.

CREATE SCHEMA IF NOT EXISTS honua;
CREATE SCHEMA IF NOT EXISTS honua_data;

-- Validates a single identifier with the same rules the create path enforces.
CREATE OR REPLACE FUNCTION honua.assert_import_identifier(value text, label text)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF value IS NULL OR length(trim(value)) = 0 THEN
        RAISE EXCEPTION '% cannot be null or empty', label;
    END IF;

    IF length(value) > 63 THEN
        RAISE EXCEPTION '% exceeds PostgreSQL identifier limit of 63 characters', label;
    END IF;

    IF value !~ '^[a-zA-Z_][a-zA-Z0-9_]*$' THEN
        RAISE EXCEPTION '% must start with a letter or underscore and contain only letters, digits, and underscores', label;
    END IF;
END;
$$;

-- Create-if-not-exists variant of create_import_table for the append/upsert load
-- modes, which must NOT drop an existing target. Builds the same fixed import shape
-- (id SERIAL PK, geometry, properties JSONB) only when the table is missing, leaving
-- any existing rows untouched.
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
        'CREATE TABLE IF NOT EXISTS %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, table_name, target_srid);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || table_name || '_geometry', schema_name, table_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || table_name || '_properties', schema_name, table_name);
END;
$$;

-- Builds an EMPTY staging sibling for a transactional replace. The staging table
-- has the same fixed import shape (id SERIAL PK, geometry, properties JSONB) and a
-- predictable name (<table>__staging) so the load can stream into it and the swap
-- can rename it over the live table. Unlike create_import_table this does NOT touch
-- the live target — the live table is only replaced atomically by swap_import_table
-- after the load fully succeeds, so a failed/cancelled load leaves the live table
-- intact.
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

    staging_name := table_name || '__staging';
    IF length(staging_name) > 63 THEN
        RAISE EXCEPTION 'Staging table name exceeds PostgreSQL identifier limit of 63 characters';
    END IF;

    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
    -- A leftover staging table from a previously-crashed load is safe to drop: it
    -- is never the live target and only ever holds in-flight, not-yet-swapped rows.
    EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, staging_name);
    EXECUTE format(
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, staging_name, target_srid);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || staging_name || '_geometry', schema_name, staging_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || staging_name || '_properties', schema_name, staging_name);

    RETURN staging_name;
END;
$$;

-- Atomically replaces the live target table with a freshly-loaded staging sibling.
-- Run inside the caller's transaction so the drop+rename is all-or-nothing: callers
-- BEGIN, load into <table>__staging, then call this; on commit the old table is gone
-- and the staging table has taken its name. A rollback leaves the live table intact.
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

    staging_name := table_name || '__staging';

    EXECUTE format('DROP TABLE IF EXISTS %I.%I CASCADE', schema_name, table_name);
    EXECUTE format('ALTER TABLE %I.%I RENAME TO %I', schema_name, staging_name, table_name);

    -- Rename the staging indexes onto the canonical names so a later load (which
    -- references idx_<table>_geometry / idx_<table>_properties) does not collide.
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_geometry', 'idx_' || table_name || '_geometry');
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_properties', 'idx_' || table_name || '_properties');
END;
$$;

-- Ensures a stable unique index over one or more JSONB property keys so the keyed
-- upsert path has a valid ON CONFLICT target. The index is expression-based over
-- properties->>'<key>' for each key column, matching the way insert_import_feature
-- stores attributes. Idempotent: re-running with the same keys is a no-op.
CREATE OR REPLACE FUNCTION honua.ensure_import_upsert_key(
    schema_name text,
    table_name text,
    key_columns text[])
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    index_name text;
    key_expression text;
    key text;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');

    IF key_columns IS NULL OR array_length(key_columns, 1) IS NULL THEN
        RAISE EXCEPTION 'Upsert load mode requires at least one key column';
    END IF;

    key_expression := '';
    FOREACH key IN ARRAY key_columns
    LOOP
        IF key IS NULL OR length(trim(key)) = 0 THEN
            RAISE EXCEPTION 'Upsert key column cannot be null or empty';
        END IF;

        IF length(key_expression) > 0 THEN
            key_expression := key_expression || ', ';
        END IF;

        -- %L safely quotes the key as a literal inside the expression index body.
        key_expression := key_expression || format('(properties->>%L)', key);
    END LOOP;

    index_name := 'uq_' || table_name || '_import_key';
    IF length(index_name) > 63 THEN
        index_name := 'uq_import_key_' || md5(table_name);
    END IF;

    EXECUTE format(
        'CREATE UNIQUE INDEX IF NOT EXISTS %I ON %I.%I (%s)',
        index_name, schema_name, table_name, key_expression);

    RETURN index_name;
END;
$$;

-- Keyed upsert: insert a feature, or UPDATE the existing row whose property key(s)
-- collide, WITHOUT dropping the table. The ON CONFLICT target is the expression
-- index built by ensure_import_upsert_key over the same property keys. Geometry and
-- properties of the conflicting row are refreshed from the incoming feature.
CREATE OR REPLACE FUNCTION honua.upsert_import_feature(
    schema_name text,
    table_name text,
    wkb bytea,
    source_srid integer,
    target_srid integer,
    properties jsonb,
    key_columns text[])
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    conflict_expression text;
    key text;
BEGIN
    IF key_columns IS NULL OR array_length(key_columns, 1) IS NULL THEN
        RAISE EXCEPTION 'Upsert load mode requires at least one key column';
    END IF;

    conflict_expression := '';
    FOREACH key IN ARRAY key_columns
    LOOP
        IF length(conflict_expression) > 0 THEN
            conflict_expression := conflict_expression || ', ';
        END IF;

        conflict_expression := conflict_expression || format('(properties->>%L)', key);
    END LOOP;

    EXECUTE format(
        'INSERT INTO %I.%I (geometry, properties) ' ||
        'VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4) ' ||
        'ON CONFLICT (%s) DO UPDATE SET ' ||
        'geometry = EXCLUDED.geometry, properties = EXCLUDED.properties',
        schema_name, table_name, conflict_expression)
    USING wkb, source_srid, target_srid, properties;
END;
$$;

-- Batch keyed upsert mirroring honua.bulk_insert_import_features: a single
-- unnest-driven INSERT ... ON CONFLICT (...) DO UPDATE so the streaming fast path
-- can merge a whole batch without dropping the target. The ON CONFLICT target is the
-- same expression list (properties->>'<key>', ...) the unique index in
-- honua.ensure_import_upsert_key is built over, so a batch row whose key(s) collide
-- with an existing row refreshes that row's geometry/properties; the rest insert.
-- Keeping the DDL/SQL text inside the function body keeps application SQL static and
-- lets identifiers be handled with format(%I)/key literals with format(%L).
CREATE OR REPLACE FUNCTION honua.bulk_upsert_import_features(
    schema_name text,
    table_name text,
    wkb_array bytea[],
    source_srid_array integer[],
    target_srid integer,
    properties_array jsonb[],
    key_columns text[],
    datum_source_srid integer DEFAULT NULL,
    datum_transformation_pipeline text DEFAULT NULL)
RETURNS TABLE(processed_count integer, failed_count integer)
LANGUAGE plpgsql
AS $$
DECLARE
    max_i integer;
    processed integer := 0;
    conflict_expression text;   -- over the live table's "properties" column (ON CONFLICT target)
    distinct_expression text;   -- the same keys over the unnest alias "props" (DISTINCT ON)
    geometry_expression text;   -- ST_Transform shape, optionally datum-pipelined
    key text;
BEGIN
    max_i := array_length(wkb_array, 1);

    IF max_i IS NULL THEN
        RETURN QUERY SELECT 0, 0;
        RETURN;
    END IF;

    IF array_length(source_srid_array, 1) != max_i OR
       array_length(properties_array, 1) != max_i THEN
        RAISE EXCEPTION 'Input arrays must have the same length';
    END IF;

    IF key_columns IS NULL OR array_length(key_columns, 1) IS NULL THEN
        RAISE EXCEPTION 'Upsert load mode requires at least one key column';
    END IF;

    conflict_expression := '';
    distinct_expression := '';
    FOREACH key IN ARRAY key_columns
    LOOP
        IF key IS NULL OR length(trim(key)) = 0 THEN
            RAISE EXCEPTION 'Upsert key column cannot be null or empty';
        END IF;

        IF length(conflict_expression) > 0 THEN
            conflict_expression := conflict_expression || ', ';
            distinct_expression := distinct_expression || ', ';
        END IF;

        conflict_expression := conflict_expression || format('(properties->>%L)', key);
        distinct_expression := distinct_expression || format('(props->>%L)', key);
    END LOOP;

    -- Mirror the insert fast-path geometry expression: rows whose source SRID matches the
    -- resolved (datum_source_srid -> target_srid) pair get the explicit Esri-parity
    -- 3-argument ST_Transform; everything else keeps PROJ's default 2-argument transform.
    -- %L safely quotes the pipeline literal; a NULL pipeline collapses to the bare form.
    IF datum_transformation_pipeline IS NULL OR length(datum_transformation_pipeline) = 0 THEN
        geometry_expression :=
            format('CASE WHEN wkb IS NOT NULL AND srid > 0 THEN ST_Transform(ST_GeomFromWKB(wkb, srid), %s) ELSE NULL END', target_srid);
    ELSE
        geometry_expression := format(
            'CASE WHEN wkb IS NULL OR srid <= 0 THEN NULL ' ||
            'WHEN srid = %s THEN ST_Transform(ST_GeomFromWKB(wkb, srid), %L, %s) ' ||
            'ELSE ST_Transform(ST_GeomFromWKB(wkb, srid), %s) END',
            datum_source_srid, datum_transformation_pipeline, target_srid, target_srid);
    END IF;

    -- DISTINCT ON keeps one row per key tuple within the batch so a single statement
    -- cannot try to update the same conflict target twice (Postgres raises "ON CONFLICT
    -- DO UPDATE command cannot affect row a second time" otherwise).
    EXECUTE format(
        'INSERT INTO %I.%I (geometry, properties) ' ||
        'SELECT DISTINCT ON (%s) %s, props ' ||
        'FROM unnest($1, $2, $3) AS t(wkb, srid, props) ' ||
        'ON CONFLICT (%s) DO UPDATE SET ' ||
        '       geometry = EXCLUDED.geometry, properties = EXCLUDED.properties',
        schema_name, table_name,
        distinct_expression,
        geometry_expression,
        conflict_expression)
    USING wkb_array, source_srid_array, properties_array;

    GET DIAGNOSTICS processed = ROW_COUNT;

    RETURN QUERY SELECT processed, 0;
END;
$$;
