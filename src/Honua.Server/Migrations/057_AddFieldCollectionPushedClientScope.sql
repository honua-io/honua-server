-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 057_AddFieldCollectionPushedClientScope.sql
-- Description: Scope the FieldCollection push idempotency log by client (#894).
--              Originally the idempotency record was keyed on change_id ALONE
--              (PRIMARY KEY (change_id) in 024_AddFieldCollectionSync.sql). Two
--              field devices that share one API key can mint a colliding
--              change_id; the second device's push then replayed the first
--              device's stored response and silently dropped the edit. This
--              migration re-keys the table on (client_id, change_id) so each
--              client's change_id namespace is independent.
-- Dependencies: Requires honua.fieldcollection_pushed_changes
--               (024_AddFieldCollectionSync.sql).

-- honua:compatibility-review reviewer=mike.mcdougall ticket=honua-server#894 reason=Backfills the new client_id column for all
--   existing rows to the 'default' sentinel (the same value the push endpoint
--   uses for callers that send no X-Honua-Client-Id header) BEFORE applying SET
--   NOT NULL, so no existing row violates the constraint. The PK is widened from
--   (change_id) to (client_id, change_id); every prior row had a unique change_id
--   so the widened key cannot collide for historical data. Rollout-safe:
--   pre-deploy readers that still SELECT by change_id alone keep matching their
--   own 'default'-scoped rows, and the new composite key only adds, never
--   removes, distinguishing information.

-- Additive first: add the column nullable with the 'default' sentinel so the
-- backfill of existing rows is implicit and new inserts during a mixed-version
-- rollout still satisfy the column.
ALTER TABLE honua.fieldcollection_pushed_changes
    ADD COLUMN IF NOT EXISTS client_id TEXT NOT NULL DEFAULT 'default';

-- Re-key on (client_id, change_id). Drop the single-column PK and create the
-- composite one. IF EXISTS / IF NOT EXISTS keep the migration idempotent if it
-- is re-applied after a partial run.
ALTER TABLE honua.fieldcollection_pushed_changes
    DROP CONSTRAINT IF EXISTS fieldcollection_pushed_changes_pkey;

ALTER TABLE honua.fieldcollection_pushed_changes
    ADD CONSTRAINT fieldcollection_pushed_changes_pkey
        PRIMARY KEY (client_id, change_id);

COMMENT ON COLUMN honua.fieldcollection_pushed_changes.client_id
    IS 'Per-client scope for idempotency. Derived from the authenticated principal / X-Honua-Client-Id header; ''default'' for callers that send no client id (#894).';
