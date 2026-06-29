-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Fix import replace-mode for long table names. honua.create_import_staging_table
-- (migration 070) derived the staging sibling name as "<table>__staging" and RAISEd
-- a hard P0001 error whenever that exceeded PostgreSQL's 63-character identifier
-- limit. A perfectly valid live target name (<= 63 chars) whose length is >= 55 then
-- could never be imported in the default "replace" load mode, because appending the
-- 9-character "__staging" suffix overflowed the limit. (Ad-hoc imports such as
-- "imported_custom_schema_<32-hex>" hit exactly this.) honua.ensure_import_upsert_key
-- already solved the same class of problem for its unique-index name with an md5
-- fallback; this migration applies the same deterministic, bounded strategy to the
-- staging table name and shares it between create_import_staging_table and
-- swap_import_table (which MUST compute the identical name) via a single helper.
--
-- The staging name is bounded to 48 characters so that the derived staging index
-- names ("idx_<staging>_geometry" and "idx_<staging>_properties", up to
-- 4 + 48 + 11 = 63) also stay within the identifier limit and can be renamed onto
-- their canonical "<table>" forms during the swap.

CREATE SCHEMA IF NOT EXISTS honua;

-- Deterministic, collision-resistant staging-table name bounded to <= 48 characters.
-- Short names keep the readable "<table>__staging" form; long names fall back to a
-- truncated readable prefix plus a stable md5-derived suffix so the same input always
-- yields the same staging name (create and swap therefore agree).
CREATE OR REPLACE FUNCTION honua.import_staging_table_name(table_name text)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE
        WHEN length(table_name) + 9 <= 48
            THEN table_name || '__staging'
        ELSE left(table_name, 33) || '_' || substr(md5(table_name), 1, 10) || '_stg'
    END;
$$;

-- Builds an EMPTY staging sibling for a transactional replace, now using the bounded
-- helper for the staging name so long-but-valid target names no longer fail.
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
    -- Defensive: the helper bounds to 48, but validate the final identifier shape so a
    -- future change to the naming strategy cannot silently emit an invalid identifier.
    PERFORM honua.assert_import_identifier(staging_name, 'Staging table name');

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

-- Atomically replaces the live target with the staging sibling. Recomputes the staging
-- name through the shared helper so it matches create_import_staging_table exactly.
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
