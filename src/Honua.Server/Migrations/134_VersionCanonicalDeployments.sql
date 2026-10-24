-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Migration 082 created the canonical promotion family in honua even when
-- Database:Schema differed. Resolve its existing physical location without moving,
-- copying, or splitting records. Runtime readers/writers apply the same fail-closed
-- complete-family rule. All changes here are additive for the previous reader.
DO $$
DECLARE
    configured_schema text := (
        SELECT nspname FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    physical_schema text;
    configured_count integer;
    legacy_count integer;
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

    -- Compare-and-set revision lives on the same unchanged deployment object.
    -- Existing reader-only records begin at zero and remain readable.
    EXECUTE format('ALTER TABLE %I.promotion_deployments ADD COLUMN IF NOT EXISTS version BIGINT NOT NULL DEFAULT 0',
        physical_schema);
END
$$;

-- A Studio published pointer and its canonical deployment are committed together. The
-- nullable link lets old and non-hosted publication requests remain unchanged.
ALTER TABLE $HonuaSchema$.studio_publication_requests
    ADD COLUMN IF NOT EXISTS deployment_id TEXT NULL;

-- Preserve the native-vs-bridged routing decision for exact request-ID replay.
ALTER TABLE $HonuaSchema$.studio_publication_requests
    ADD COLUMN IF NOT EXISTS governs_studio_routes BOOLEAN NOT NULL DEFAULT TRUE;

-- The accepted request that last moved the item's published pointer determines its
-- current route. Request timestamps can be inverted by concurrent approvals or clocks.
-- Legacy rows remain nullable and use the preexisting timestamp fallback.
ALTER TABLE $HonuaSchema$.studio_content_items
    ADD COLUMN IF NOT EXISTS governing_publication_request_id UUID NULL;

-- A published-pointer rollback can create a successor on the accepted route.
-- Keep that successor's identity on its own immutable request receipt.
ALTER TABLE $HonuaSchema$.studio_rollback_requests
    ADD COLUMN IF NOT EXISTS deployment_id TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_rollback_requests_deployment
    ON $HonuaSchema$.studio_rollback_requests (deployment_id)
    WHERE deployment_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_publication_requests_deployment
    ON $HonuaSchema$.studio_publication_requests (deployment_id)
    WHERE deployment_id IS NOT NULL;
