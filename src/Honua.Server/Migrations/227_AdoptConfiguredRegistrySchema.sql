-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Move the registry without changing table identity, foreign keys, or owned sequences.
-- honua:migration-phase contract
-- honua:compatibility-review reason=Registry schema adoption requires old nodes drained before moving tables

CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

DO $$
DECLARE
    target_schema text := (SELECT nspname FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    family text[] := ARRAY['services', 'layers', 'service_layers', 'layer_fields'];
    source_count integer;
    target_count integer;
    table_name text;
BEGIN
    IF target_schema = 'honua' THEN
        RETURN;
    END IF;

    SELECT count(*) INTO source_count FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', 'honua', item.table_name)) IS NOT NULL;
    SELECT count(*) INTO target_count FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', target_schema, item.table_name)) IS NOT NULL;

    IF target_count = cardinality(family) AND source_count = 0 THEN
        RETURN;
    END IF;
    IF target_count > 0 OR source_count <> cardinality(family) THEN
        RAISE EXCEPTION
            'Cannot adopt registry schema: % legacy tables in honua and % target tables in %; expected one complete family',
            source_count, target_count, target_schema;
    END IF;

    FOREACH table_name IN ARRAY family LOOP
        EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', 'honua', table_name, target_schema);
    END LOOP;
END
$$;

CREATE OR REPLACE FUNCTION honua.resolve_feature_public_objectid(
    target_layer_id INT,
    storage_objectid BIGINT,
    row_attributes JSONB)
RETURNS BIGINT AS $$
DECLARE
    primary_id_field TEXT;
    raw_public_objectid TEXT;
    resolved_public_objectid BIGINT;
BEGIN
    SELECT NULLIF(primary_key_column, '')
    INTO primary_id_field
    FROM $HonuaSchema$.layers
    WHERE layer_id = target_layer_id;

    IF primary_id_field IS NULL
       OR lower(primary_id_field) = 'objectid'
       OR row_attributes IS NULL
       OR jsonb_typeof(row_attributes) <> 'object' THEN
        RETURN storage_objectid;
    END IF;

    raw_public_objectid := row_attributes ->> primary_id_field;
    IF raw_public_objectid IS NULL THEN
        SELECT value
        INTO raw_public_objectid
        FROM jsonb_each_text(row_attributes)
        WHERE lower(key) = lower(primary_id_field)
        LIMIT 1;
    END IF;

    IF raw_public_objectid IS NULL OR raw_public_objectid !~ '^[+-]?[0-9]+$' THEN
        RETURN storage_objectid;
    END IF;

    BEGIN
        resolved_public_objectid := raw_public_objectid::BIGINT;
    EXCEPTION
        WHEN invalid_text_representation OR numeric_value_out_of_range THEN
            RETURN storage_objectid;
    END;

    RETURN resolved_public_objectid;
END;
$$ LANGUAGE plpgsql STABLE;
