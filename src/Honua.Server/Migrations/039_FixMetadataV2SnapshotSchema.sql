-- Corrective for 031_CreateMetadataV2Snapshot.sql.
--
-- 031 created the Metadata-v2 snapshot/current/index tables with UNQUALIFIED
-- names (`CREATE TABLE IF NOT EXISTS metadata_v2_snapshots ...`). On deployments
-- where the migration search_path resolved to `public` (unlike the qualified
-- `honua.` tables created by 034 and the rest), these tables landed in `public`.
-- The Postgres metadata-v2 graph store reads `honua.metadata_v2_snapshots`, so it
-- found nothing and EVERY GeoServices/OGC service returned 503 on a fresh deploy.
-- (TestKit/base-schema masks this because it creates the tables qualified.)
--
-- 031 is now qualified for fresh deploys; this migration relocates any tables a
-- previous (buggy) 031 left in `public` into `honua`. Idempotent and safe to
-- re-run: only moves a table that exists in `public` and not yet in `honua`.

DO $$
DECLARE
    target text;
BEGIN
    FOREACH target IN ARRAY ARRAY[
        'metadata_v2_snapshots',
        'metadata_v2_current',
        'metadata_v2_resources_idx',
        'metadata_v2_services_idx',
        'metadata_v2_publications_idx',
        'metadata_v2_storage_bindings_idx',
        'metadata_v2_connections_idx'
    ]
    LOOP
        IF EXISTS (
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = target
        ) AND NOT EXISTS (
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'honua' AND table_name = target
        ) THEN
            EXECUTE format('ALTER TABLE public.%I SET SCHEMA honua', target);
        END IF;
    END LOOP;
END $$;
