-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 053_AddImportDatumTransformation.sql
-- Route the file-import reprojection path through the same explicit datum-pipeline
-- selection used by the query path (#1501, follow-up to #1274). The original
-- honua.insert_import_feature reprojects with the bare 2-argument ST_Transform, so
-- PROJ silently picks its own "best available" pipeline rather than the auditable
-- Esri-default geotransformation resolved by IDatumTransformationCatalog. This adds
-- an additive overload that accepts an optional PROJ pipeline string and emits the
-- explicit 3-argument ST_Transform(geom, '<pipeline>', toSrid) form — the same shape
-- the query builders emit through DatumTransformSql.BuildTransformExpression.
--
-- The existing 6-argument overload is left untouched for backward compatibility; the
-- import service calls this 7-argument overload only when a pipeline has actually been
-- resolved for the (sourceSrid -> targetSrid) pair, so imports with no curated default
-- keep their current behavior byte-for-byte.

CREATE SCHEMA IF NOT EXISTS honua;

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
    -- A null/empty pipeline keeps PROJ's default (2-argument) behavior; a non-empty
    -- pipeline forces the explicit Esri-parity pipeline via the 3-argument overload.
    IF datum_transformation_pipeline IS NULL OR length(datum_transformation_pipeline) = 0 THEN
        EXECUTE format(
            'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $3), $4)',
            schema_name, table_name)
        USING wkb, source_srid, target_srid, properties;
    ELSE
        EXECUTE format(
            'INSERT INTO %I.%I (geometry, properties) VALUES (ST_Transform(ST_GeomFromWKB($1, $2), $4, $3), $5)',
            schema_name, table_name)
        USING wkb, source_srid, target_srid, datum_transformation_pipeline, properties;
    END IF;
END;
$$;
