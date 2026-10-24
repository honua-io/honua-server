-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 123_ImportDatumTransformPipeline.sql
-- The 7-argument honua.insert_import_feature and honua.bulk_upsert_import_features
-- passed a PROJ operation pipeline to ST_Transform(geom, text, to_srid). That text
-- argument is a source CRS, not a coordinate operation, so a NADCON pipeline was
-- never applied. The query path already uses ST_TransformPipeline. Match it here.
-- An exact +proj=noop selection assigns the target SRID and keeps every ordinate,
-- the same rewrite DatumTransformSql uses.

CREATE OR REPLACE FUNCTION honua.insert_import_feature(
    schema_name text,
    table_name text,
    wkb bytea,
    source_srid integer,
    target_srid integer,
    properties jsonb,
    datum_transformation_pipeline text)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF datum_transformation_pipeline IS NULL OR length(btrim(datum_transformation_pipeline)) = 0 THEN
        EXECUTE format(
            'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4)',
            schema_name, table_name)
        USING wkb, source_srid, target_srid, properties;
    ELSIF btrim(datum_transformation_pipeline) = '+proj=noop' THEN
        EXECUTE format(
            'INSERT INTO %I.%I (geometry, properties) VALUES (ST_SetSRID(ST_GeomFromWKB($1, $2), $3), $4)',
            schema_name, table_name)
        USING wkb, source_srid, target_srid, properties;
    ELSE
        EXECUTE format(
            'INSERT INTO %I.%I (geometry, properties) VALUES (ST_TransformPipeline(ST_GeomFromWKB($1, $2), $4, $3), $5)',
            schema_name, table_name)
        USING wkb, source_srid, target_srid, datum_transformation_pipeline, properties;
    END IF;
END;
$$;

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
    conflict_expression text;
    distinct_expression text;
    geometry_expression text;
    key text;
    pipeline text := btrim(datum_transformation_pipeline);
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

    IF pipeline IS NULL OR length(pipeline) = 0 THEN
        geometry_expression :=
            format('CASE WHEN wkb IS NOT NULL AND srid > 0 THEN ST_Transform(ST_GeomFromWKB(wkb, srid), %s) ELSE NULL END', target_srid);
    ELSIF pipeline = '+proj=noop' THEN
        geometry_expression := format(
            'CASE WHEN wkb IS NULL OR srid <= 0 THEN NULL ' ||
            'WHEN srid = %s THEN ST_SetSRID(ST_GeomFromWKB(wkb, srid), %s) ' ||
            'ELSE ST_Transform(ST_GeomFromWKB(wkb, srid), %s) END',
            datum_source_srid, target_srid, target_srid);
    ELSE
        geometry_expression := format(
            'CASE WHEN wkb IS NULL OR srid <= 0 THEN NULL ' ||
            'WHEN srid = %s THEN ST_TransformPipeline(ST_GeomFromWKB(wkb, srid), %L, %s) ' ||
            'ELSE ST_Transform(ST_GeomFromWKB(wkb, srid), %s) END',
            datum_source_srid, pipeline, target_srid, target_srid);
    END IF;

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
