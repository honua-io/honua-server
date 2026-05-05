-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 024_AddFieldCollectionSync.sql
-- Description: FieldCollection mobile sync support (#894).
--              Adds tables backing the four sync endpoints used by honua-mobile
--              FieldCollection offline sync (generation, sync-cursor, pull, push).
-- Dependencies: Requires honua schema (001_CreateHonuaSchema.sql) and the
--               sync_generation sequence (012_AddReplicationDurability.sql).

-- Current state of each FieldCollection feature.
-- Conflict detection compares a push baseVersion against the row's version here.
CREATE TABLE IF NOT EXISTS honua.fieldcollection_features (
    feature_id  TEXT        NOT NULL,
    layer_id    INT         NOT NULL,
    version     BIGINT      NOT NULL,
    payload     JSONB,
    is_deleted  BOOLEAN     NOT NULL DEFAULT FALSE,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (feature_id, layer_id)
);

CREATE INDEX IF NOT EXISTS idx_fieldcollection_features_layer
    ON honua.fieldcollection_features(layer_id);

-- Append-only log of FieldCollection changes ordered by generation.
-- Pull queries scan by generation > sinceGeneration with a deterministic order.
CREATE TABLE IF NOT EXISTS honua.fieldcollection_changes (
    change_seq  BIGSERIAL   PRIMARY KEY,
    generation  BIGINT      NOT NULL,
    feature_id  TEXT        NOT NULL,
    layer_id    INT         NOT NULL,
    operation   SMALLINT    NOT NULL,
    version     BIGINT      NOT NULL,
    payload     JSONB,
    changed_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT fieldcollection_changes_valid_operation CHECK (operation IN (1, 2, 3))
);

CREATE INDEX IF NOT EXISTS idx_fieldcollection_changes_generation
    ON honua.fieldcollection_changes(generation);

CREATE INDEX IF NOT EXISTS idx_fieldcollection_changes_feature
    ON honua.fieldcollection_changes(feature_id, layer_id, generation);

-- Idempotency record for pushed changes keyed by mobile-assigned change UUID.
-- A repeat push for the same change_id replays the stored response payload
-- without re-applying.
CREATE TABLE IF NOT EXISTS honua.fieldcollection_pushed_changes (
    change_id        TEXT        NOT NULL PRIMARY KEY,
    feature_id       TEXT        NOT NULL,
    layer_id         INT         NOT NULL,
    operation        SMALLINT    NOT NULL,
    outcome          SMALLINT    NOT NULL,
    response_payload JSONB       NOT NULL,
    pushed_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT fieldcollection_pushed_changes_valid_operation CHECK (operation IN (1, 2, 3)),
    CONSTRAINT fieldcollection_pushed_changes_valid_outcome CHECK (outcome IN (1, 2, 3))
);

CREATE INDEX IF NOT EXISTS idx_fieldcollection_pushed_changes_pushed_at
    ON honua.fieldcollection_pushed_changes(pushed_at);

-- Per-client acknowledged generation cursor.
-- client_id is derived from the authenticated principal (API-key identity).
CREATE TABLE IF NOT EXISTS honua.fieldcollection_sync_cursors (
    client_id            TEXT        NOT NULL PRIMARY KEY,
    last_sync_generation BIGINT      NOT NULL DEFAULT 0,
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE  honua.fieldcollection_features            IS 'Current state of each FieldCollection feature (#894).';
COMMENT ON TABLE  honua.fieldcollection_changes             IS 'Append-only log of FieldCollection feature changes ordered by generation (#894).';
COMMENT ON TABLE  honua.fieldcollection_pushed_changes      IS 'Idempotency log for mobile-assigned change UUIDs (#894).';
COMMENT ON TABLE  honua.fieldcollection_sync_cursors        IS 'Per-client acknowledged generation cursors (#894).';
COMMENT ON COLUMN honua.fieldcollection_changes.operation   IS 'Change type: 1=insert, 2=update, 3=delete';
COMMENT ON COLUMN honua.fieldcollection_pushed_changes.outcome IS 'Outcome: 1=applied, 2=conflict, 3=rejected';
