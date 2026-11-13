-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE OR REPLACE FUNCTION $HonuaSchema$.realtime_job_status_changed()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_rows JSONB;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT jsonb_agg(jsonb_build_object(
            'scope_key', n.operation_id, 'kind', 'job.status',
            'payload', jsonb_build_object('jobId', n.operation_id, 'kind', n.kind,
                'status', n.status, 'requesterId', n.record->'audit'->>'requestedBy',
                'updatedAt', n.updated_at)) ORDER BY n.operation_id COLLATE "C")
        INTO v_rows FROM new_jobs n;
    ELSE
        SELECT jsonb_agg(jsonb_build_object(
            'scope_key', n.operation_id, 'kind', 'job.status',
            'payload', jsonb_build_object('jobId', n.operation_id, 'kind', n.kind,
                'status', n.status, 'requesterId', n.record->'audit'->>'requestedBy',
                'updatedAt', n.updated_at)) ORDER BY n.operation_id COLLATE "C")
        INTO v_rows FROM new_jobs n JOIN old_jobs o USING (operation_id)
        WHERE n.status IS DISTINCT FROM o.status;
    END IF;

    IF v_rows IS NOT NULL THEN
        PERFORM $HonuaSchema$.realtime_append('jobs', v_rows);
    END IF;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.realtime_catalog_changed()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_rows JSONB;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT jsonb_agg(jsonb_build_object(
            'scope_key', n.environment, 'kind', 'catalog.changed',
            'payload', jsonb_build_object('environment', n.environment, 'etag', n.etag,
                'previousEtag', NULL)) ORDER BY n.environment COLLATE "C")
        INTO v_rows FROM new_catalog n;
    ELSE
        SELECT jsonb_agg(jsonb_build_object(
            'scope_key', n.environment, 'kind', 'catalog.changed',
            'payload', jsonb_build_object('environment', n.environment, 'etag', n.etag,
                'previousEtag', o.etag)) ORDER BY n.environment COLLATE "C")
        INTO v_rows FROM new_catalog n JOIN old_catalog o USING (environment)
        WHERE n.etag IS DISTINCT FROM o.etag;
    END IF;

    IF v_rows IS NOT NULL THEN
        PERFORM $HonuaSchema$.realtime_append('catalog', v_rows);
    END IF;
    RETURN NULL;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
        WHERE tgrelid = '$HonuaSchema$.execution_jobs'::pg_catalog.regclass
            AND tgname = 'realtime_job_insert') THEN
        CREATE TRIGGER realtime_job_insert AFTER INSERT ON $HonuaSchema$.execution_jobs
            REFERENCING NEW TABLE AS new_jobs
            FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.realtime_job_status_changed();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
        WHERE tgrelid = '$HonuaSchema$.execution_jobs'::pg_catalog.regclass
            AND tgname = 'realtime_job_update') THEN
        CREATE TRIGGER realtime_job_update AFTER UPDATE ON $HonuaSchema$.execution_jobs
            REFERENCING OLD TABLE AS old_jobs NEW TABLE AS new_jobs
            FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.realtime_job_status_changed();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
        WHERE tgrelid = '$HonuaSchema$.metadata_v2_current'::pg_catalog.regclass
            AND tgname = 'realtime_catalog_insert') THEN
        CREATE TRIGGER realtime_catalog_insert AFTER INSERT ON $HonuaSchema$.metadata_v2_current
            REFERENCING NEW TABLE AS new_catalog
            FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.realtime_catalog_changed();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
        WHERE tgrelid = '$HonuaSchema$.metadata_v2_current'::pg_catalog.regclass
            AND tgname = 'realtime_catalog_update') THEN
        CREATE TRIGGER realtime_catalog_update AFTER UPDATE ON $HonuaSchema$.metadata_v2_current
            REFERENCING OLD TABLE AS old_catalog NEW TABLE AS new_catalog
            FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.realtime_catalog_changed();
    END IF;
END;
$$;
