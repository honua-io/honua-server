-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Forward-only adoption for deployments that selected Database:Schema before the
-- guarded migrations honored $HonuaSchema$. Those migrations were journaled while
-- their tables were created in honua; move the complete legacy families without
-- copying data or changing object identity. Partial or duplicate families fail the
-- transaction closed for operator reconciliation.
-- honua:migration-phase contract
-- honua:compatibility-review reason=#3899 requires old nodes drained before guarded tables move schemas

CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

DO $$
DECLARE
    target_schema text := (
        SELECT nspname
        FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    family text[] := ARRAY[
        'metadata_v2_snapshots',
        'metadata_v2_current',
        'metadata_v2_resources_idx',
        'metadata_v2_services_idx',
        'metadata_v2_publications_idx',
        'metadata_v2_storage_bindings_idx',
        'metadata_v2_connections_idx'];
    source_count integer;
    target_count integer;
    table_name text;
BEGIN
    IF target_schema = 'honua' THEN
        RETURN;
    END IF;

    SELECT count(*) INTO source_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', 'honua', item.table_name)) IS NOT NULL;

    SELECT count(*) INTO target_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', target_schema, item.table_name)) IS NOT NULL;

    IF target_count = cardinality(family) THEN
        IF source_count > 0 THEN
            RAISE EXCEPTION
                'Cannot adopt Metadata v2 schema: complete target family in % coexists with % legacy table(s) in honua',
                target_schema,
                source_count;
        END IF;
        RETURN;
    END IF;

    IF target_count > 0 THEN
        RAISE EXCEPTION
            'Cannot adopt Metadata v2 schema: target family in % is partial (% of % tables)',
            target_schema,
            target_count,
            cardinality(family);
    END IF;

    IF source_count <> cardinality(family) THEN
        IF EXISTS (
            SELECT 1
            FROM public.schema_versions
            WHERE scriptname = 'Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql') THEN
            RAISE EXCEPTION
                'Cannot adopt Metadata v2 schema: journaled legacy family in honua is incomplete (% of % tables)',
                source_count,
                cardinality(family);
        END IF;
        RETURN;
    END IF;

    FOREACH table_name IN ARRAY family LOOP
        EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', 'honua', table_name, target_schema);
    END LOOP;
END
$$;

DO $$
DECLARE
    target_schema text := (
        SELECT nspname
        FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    family text[] := ARRAY[
        'sta_thing',
        'sta_sensor',
        'sta_observed_property',
        'sta_datastream',
        'sta_observation',
        'sta_observation_default'];
    source_count integer;
    target_count integer;
    table_name text;
BEGIN
    IF target_schema = 'honua' THEN
        RETURN;
    END IF;

    SELECT count(*) INTO source_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', 'honua', item.table_name)) IS NOT NULL;

    SELECT count(*) INTO target_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', target_schema, item.table_name)) IS NOT NULL;

    IF target_count = cardinality(family) THEN
        IF source_count > 0 THEN
            RAISE EXCEPTION
                'Cannot adopt SensorThings schema: complete target family in % coexists with % legacy table(s) in honua',
                target_schema,
                source_count;
        END IF;
        RETURN;
    END IF;

    IF target_count > 0 THEN
        RAISE EXCEPTION
            'Cannot adopt SensorThings schema: target family in % is partial (% of % tables)',
            target_schema,
            target_count,
            cardinality(family);
    END IF;

    IF source_count <> cardinality(family) THEN
        IF EXISTS (
            SELECT 1
            FROM public.schema_versions
            WHERE scriptname = 'Honua.Server.Migrations.059_CreateSensorThings.sql') THEN
            RAISE EXCEPTION
                'Cannot adopt SensorThings schema: journaled legacy family in honua is incomplete (% of % tables)',
                source_count,
                cardinality(family);
        END IF;
        RETURN;
    END IF;

    FOREACH table_name IN ARRAY family LOOP
        IF to_regclass(format('%I.%I', 'honua', table_name)) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', 'honua', table_name, target_schema);
        END IF;
    END LOOP;
END
$$;

DO $$
DECLARE
    target_schema text := (
        SELECT nspname
        FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    family text[] := ARRAY[
        'raster_data',
        'raster_statistics',
        'raster_tiles',
        'raster_layer_statistics',
        'raster_sensor_metadata',
        'raster_overviews',
        'raster_footprints'];
    source_count integer;
    target_count integer;
    table_name text;
BEGIN
    IF target_schema = 'honua' THEN
        RETURN;
    END IF;

    SELECT count(*) INTO source_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', 'honua', item.table_name)) IS NOT NULL;

    SELECT count(*) INTO target_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', target_schema, item.table_name)) IS NOT NULL;

    IF source_count > 0 AND target_count > 0 THEN
        RAISE EXCEPTION
            'Cannot adopt raster schema: % target table(s) in % coexist with % legacy table(s) in honua',
            target_count,
            target_schema,
            source_count;
    END IF;

    IF source_count = 0 THEN
        RETURN;
    END IF;

    FOREACH table_name IN ARRAY family LOOP
        IF to_regclass(format('%I.%I', 'honua', table_name)) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', 'honua', table_name, target_schema);
        END IF;
    END LOOP;
END
$$;
