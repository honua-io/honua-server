-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 100_AddReplicaConflictClientEditSuperseded.sql
-- Description: Records that a conflict's own client edit committed but was then superseded by a later
--              edit in the same upload to the same feature (#2430). client_edit_applied is FALSE for
--              such a record because the row does not hold THIS edit's state, but it does not hold the
--              captured pre-conflict server state either, so keeping the server must perform a real
--              restore instead of the withheld-edit no-op shortcut.
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql) and
--               099_AddReplicaConflictClientEditOutcomeUnknown.sql.

-- Existing rows predate multi-edit attribution and were never marked superseded, so FALSE is both the
-- correct backfill and the correct default.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS client_edit_superseded BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN honua.replica_conflicts.client_edit_superseded IS
    'Whether this conflict edit committed but was overwritten by a later edit in the same upload (#2430)';
