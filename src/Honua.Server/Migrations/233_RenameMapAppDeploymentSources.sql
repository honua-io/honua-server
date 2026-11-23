-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:reader-incompatible reason=canonical Map/App deployment source kinds replace kinds required by prior readers
-- Reader-incompatible: restore the pre-upgrade backup and matching server to roll back.
-- Resolve the complete physical promotion family before updating indexed and JSON
-- source names together. IDs, CAS revisions, timestamps and opaque targets are unchanged.
DO $$
DECLARE
    configured_schema text := (
        SELECT nspname FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    physical_schema text;
    configured_count integer;
    legacy_count integer;
    conflicting_identity boolean;
    invalid_source boolean;
    caller_search_path text := current_setting('search_path');
BEGIN
    IF configured_schema IS NULL THEN
        RAISE EXCEPTION 'Canonical promotion family configured schema is absent';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN (configured_schema, 'honua')
          AND c.relname IN ('promotion_deployments', 'promotion_published_services')
          AND c.relkind NOT IN ('r', 'p')) THEN
        RAISE EXCEPTION 'Canonical promotion family contains a non-table object';
    END IF;
    SELECT count(*) INTO configured_count
    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = configured_schema
      AND c.relname IN ('promotion_deployments', 'promotion_published_services');
    SELECT count(*) INTO legacy_count
    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'honua' AND n.nspname <> configured_schema
      AND c.relname IN ('promotion_deployments', 'promotion_published_services');
    IF configured_count = 2 AND legacy_count = 0 THEN
        physical_schema := configured_schema;
    ELSIF configured_count = 0 AND legacy_count = 2 THEN
        physical_schema := 'honua';
    ELSE
        RAISE EXCEPTION 'Canonical promotion family is absent, partial, or ambiguous (configured %, legacy %)',
            configured_count, legacy_count;
    END IF;
    -- The migration runner substitutes dollar-delimited tokens, so the resolved schema is
    -- selected through search_path instead of tagged dynamic SQL; restored before END.
    PERFORM set_config('search_path', format('%I, pg_catalog, pg_temp', physical_schema), true);

    SELECT EXISTS (
            SELECT 1 FROM promotion_deployments
            WHERE source_kind IN ('MapPackage', 'AppPackage')
              AND (jsonb_typeof(document) IS DISTINCT FROM 'object'
                OR jsonb_typeof(document -> 'source') IS DISTINCT FROM 'object'))
        INTO invalid_source;
    IF invalid_source THEN
        RAISE EXCEPTION 'Deployment source must be an object before its vocabulary can be migrated';
    END IF;

    SELECT EXISTS (
            SELECT 1 FROM promotion_deployments
            WHERE ((document -> 'source') ? 'mapPackageId' AND (document -> 'source') ? 'mapId'
                AND document #> '{source,mapPackageId}' IS DISTINCT FROM document #> '{source,mapId}')
               OR ((document -> 'source') ? 'appPackageId' AND (document -> 'source') ? 'appId'
                AND document #> '{source,appPackageId}' IS DISTINCT FROM document #> '{source,appId}'))
        INTO conflicting_identity;
    IF conflicting_identity THEN
        RAISE EXCEPTION 'Deployment source contains conflicting legacy and canonical identities';
    END IF;

    UPDATE promotion_deployments
        SET source_kind = CASE source_kind WHEN 'MapPackage' THEN 'Map'
                WHEN 'AppPackage' THEN 'App' ELSE source_kind END,
            document = jsonb_set(document, '{source}',
                ((document -> 'source') - 'mapPackageId' - 'appPackageId')
                || CASE WHEN (document -> 'source') ? 'mapPackageId'
                    THEN jsonb_build_object('mapId', document #> '{source,mapPackageId}') ELSE '{}'::jsonb END
                || CASE WHEN (document -> 'source') ? 'appPackageId'
                    THEN jsonb_build_object('appId', document #> '{source,appPackageId}') ELSE '{}'::jsonb END
                || CASE document #>> '{source,kind}'
                    WHEN 'map_package' THEN '{"kind":"map"}'::jsonb
                    WHEN 'app_package' THEN '{"kind":"app"}'::jsonb ELSE '{}'::jsonb END)
        WHERE source_kind IN ('MapPackage', 'AppPackage')
           OR document #>> '{source,kind}' IN ('map_package', 'app_package')
           OR (document -> 'source') ? 'mapPackageId' OR (document -> 'source') ? 'appPackageId';
    PERFORM set_config('search_path', caller_search_path, true);
END
$$;
