-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Committed job-artifact import receipt. The row is reserved before the import and updated
-- to committed in the same transaction that merges staged rows into the live table, so a
-- crash cannot replay an append that already committed. scope_key is a server hash of the
-- tenant, actor, and intent, not the client key alone.
-- Expand-only: a new table; nothing existing is altered.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.job_artifact_import_receipt (
    scope_key text PRIMARY KEY,
    tenant_id text NOT NULL,
    actor_id text NOT NULL,
    intent_id text NOT NULL,
    fingerprint text NOT NULL,
    phase text NOT NULL,
    schema_name text NOT NULL,
    table_name text NOT NULL,
    physical_table text,
    feature_count integer,
    source_srid integer,
    target_srid integer,
    content_sha256 text,
    crs_state text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT job_artifact_import_receipt_scope_key CHECK (char_length(scope_key) = 64),
    CONSTRAINT job_artifact_import_receipt_tenant CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    CONSTRAINT job_artifact_import_receipt_actor CHECK (char_length(actor_id) BETWEEN 1 AND 128),
    CONSTRAINT job_artifact_import_receipt_intent CHECK (char_length(intent_id) BETWEEN 1 AND 128),
    CONSTRAINT job_artifact_import_receipt_fingerprint CHECK (char_length(fingerprint) = 64),
    CONSTRAINT job_artifact_import_receipt_phase CHECK (phase IN ('reserved', 'committed')),
    CONSTRAINT job_artifact_import_receipt_names CHECK (
        char_length(schema_name) BETWEEN 1 AND 63
        AND char_length(table_name) BETWEEN 1 AND 63),
    CONSTRAINT job_artifact_import_receipt_committed_shape CHECK (
        phase = 'reserved'
        OR (
            physical_table IS NOT NULL
            AND char_length(physical_table) BETWEEN 1 AND 63
            AND feature_count > 0
            AND source_srid > 0
            AND target_srid > 0
            AND content_sha256 IS NOT NULL
            AND char_length(content_sha256) = 64
            AND crs_state IN ('known', 'unknown')))
);

COMMENT ON TABLE $HonuaSchema$.job_artifact_import_receipt IS
    'Job-artifact import idempotency receipt. Updated to committed in the same transaction as the imported rows.';
