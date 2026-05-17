-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Bulk import helper functions for memory-efficient feature insertion

CREATE OR REPLACE FUNCTION honua.bulk_insert_import_features(
    schema_name text,
    table_name text,
    wkb_array bytea[],
    source_srid_array integer[],
    target_srid integer,
    properties_array jsonb[])
RETURNS TABLE(processed_count integer, failed_count integer)
LANGUAGE plpgsql
AS $$
DECLARE
    i integer := 1;
    max_i integer;
    processed integer := 0;
    failed integer := 0;
BEGIN
    -- Validate arrays have same length
    max_i := array_length(wkb_array, 1);

    IF max_i IS NULL OR
       array_length(source_srid_array, 1) != max_i OR
       array_length(properties_array, 1) != max_i THEN
        RAISE EXCEPTION 'Input arrays must have the same length';
    END IF;

    -- Bulk insert using unnest for optimal performance
    BEGIN
        EXECUTE format(
            'INSERT INTO %I.%I (geometry, properties)
             SELECT CASE
                        WHEN wkb IS NOT NULL AND srid > 0
                        THEN ST_Transform(ST_GeomFromWKB(wkb, srid), %s)
                        ELSE NULL
                    END,
                    props
             FROM unnest($1, $2, $3) AS t(wkb, srid, props)',
            schema_name, table_name, target_srid)
        USING wkb_array, source_srid_array, properties_array;

        processed := max_i;
    EXCEPTION
        WHEN OTHERS THEN
            -- Fall back to individual inserts for error handling
            WHILE i <= max_i LOOP
                BEGIN
                    EXECUTE format(
                        'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4)',
                        schema_name, table_name)
                    USING wkb_array[i], source_srid_array[i], target_srid, properties_array[i];
                    processed := processed + 1;
                EXCEPTION
                    WHEN OTHERS THEN
                        failed := failed + 1;
                END;
                i := i + 1;
            END LOOP;
    END;

    RETURN QUERY SELECT processed, failed;
END;
$$;

CREATE OR REPLACE FUNCTION honua.bulk_insert_import_features(
    table_name text,
    wkb_array bytea[],
    source_srid_array integer[],
    target_srid integer,
    properties_array jsonb[])
RETURNS TABLE(processed_count integer, failed_count integer)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT *
    FROM honua.bulk_insert_import_features(
        'honua_data',
        table_name,
        wkb_array,
        source_srid_array,
        target_srid,
        properties_array);
END;
$$;

-- Function for COPY-based bulk insert (highest performance)
CREATE OR REPLACE FUNCTION honua.prepare_bulk_copy_table(table_name text)
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    temp_table_name text;
BEGIN
    IF table_name IS NULL OR length(trim(table_name)) = 0 THEN
        RAISE EXCEPTION 'Table name cannot be null or empty';
    END IF;

    temp_table_name := table_name || '_copy_temp';

    -- Drop temp table if exists
    EXECUTE format('DROP TABLE IF EXISTS %I', temp_table_name);

    -- Create temp table with same structure
    EXECUTE format(
        'CREATE TEMP TABLE %I (
            wkb bytea,
            source_srid integer,
            target_srid integer,
            properties jsonb
        )',
        temp_table_name);

    RETURN temp_table_name;
END;
$$;

CREATE OR REPLACE FUNCTION honua.finalize_bulk_copy(
    source_table_name text,
    target_table_name text)
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    inserted_count integer;
BEGIN
    -- Insert from temp table to target table with geometry transformation
    EXECUTE format(
        'INSERT INTO %I (geometry, properties)
         SELECT CASE
                    WHEN wkb IS NOT NULL AND source_srid > 0
                    THEN ST_Transform(ST_GeomFromWKB(wkb, source_srid), target_srid)
                    ELSE NULL
                END,
                properties
         FROM %I',
        target_table_name, source_table_name);

    GET DIAGNOSTICS inserted_count = ROW_COUNT;

    -- Clean up temp table
    EXECUTE format('DROP TABLE %I', source_table_name);

    RETURN inserted_count;
END;
$$;
