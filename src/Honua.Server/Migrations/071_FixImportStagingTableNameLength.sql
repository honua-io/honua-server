-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Fix: the transactional-replace staging path (migration 070) hard-fails for
-- valid-but-long target table names. honua.create_import_staging_table derived
-- the staging name as `<table>__staging` and RAISEd when it exceeded the 63-char
-- PostgreSQL identifier limit. Because the default import load mode is Replace,
-- this rejected every import whose physical table name is >= 55 characters
-- (e.g. the `imported_<requested>` names the import endpoint generates), even
-- though such names are perfectly valid on their own — the original, non-staging
-- create path (migration 004) only capped the base table name at 63 and let
-- PostgreSQL silently truncate the derived index identifiers.
--
-- This migration makes the staging name length-safe by falling back to a short,
-- deterministic md5-based name when the natural `<table>__staging` form would
-- exceed 63 characters, mirroring the existing fallback in
-- honua.ensure_import_upsert_key. The derivation is factored into a single
-- IMMUTABLE helper so create_import_staging_table and swap_import_table always
-- agree on the staging table's name. Short names keep the exact, unchanged
-- `<table>__staging` form, so existing imports are unaffected.

CREATE SCHEMA IF NOT EXISTS honua;

-- Single source of truth for the staging-table name so the create and swap
-- functions never disagree. Deterministic and length-safe (<= 36 chars in the
-- fallback form: 'stg_' + 32-char md5).
CREATE OR REPLACE FUNCTION honua.import_staging_table_name(table_name text)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    staging_name text;
BEGIN
    staging_name := table_name || '__staging';

    -- A truncated `<table>__staging` could collide with the live target or
    -- another staging sibling, so when the natural name would exceed the 63-char
    -- identifier limit, use a short, deterministic md5-based name instead.
    IF length(staging_name) > 63 THEN
        staging_name := 'stg_' || md5(table_name);
    END IF;

    RETURN staging_name;
END;
$$;

-- Redefine the staging-create function to use the length-safe helper instead of
-- RAISEing on long names. Behaviour for short names is unchanged.
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
        'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW())',
        schema_name, staging_name, target_srid);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIST (geometry)', 'idx_' || staging_name || '_geometry', schema_name, staging_name);
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I USING GIN (properties)', 'idx_' || staging_name || '_properties', schema_name, staging_name);

    RETURN staging_name;
END;
$$;

-- Redefine the swap function to resolve the staging name through the same helper
-- so it always finds the table that create_import_staging_table built.
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
    -- PostgreSQL truncates these derived names to 63 chars exactly as the live
    -- create path relies on.
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_geometry', 'idx_' || table_name || '_geometry');
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_properties', 'idx_' || table_name || '_properties');
END;
$$;
