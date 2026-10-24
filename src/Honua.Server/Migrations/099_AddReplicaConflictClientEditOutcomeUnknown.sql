-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 099_AddReplicaConflictClientEditOutcomeUnknown.sql
-- Description: Records that the storage layer could not say whether a conflicting client edit
--              committed (#2430). The shared edit pipeline reports this explicitly when a transaction's
--              commit acknowledgement is lost, and collapsing it into client_edit_applied = FALSE let a
--              later keepServer resolution plan a no-op while the client overwrite may in fact have
--              been in place. A conflict carrying this flag makes the resolution planner write in both
--              directions instead of taking either no-op shortcut.
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql) and
--               098_AddReplicaConflictClaimIdentity.sql.

-- Existing rows predate the indeterminate outcome and were recorded from a definite result, so FALSE
-- is the correct backfill as well as the correct default.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS client_edit_outcome_unknown BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN honua.replica_conflicts.client_edit_outcome_unknown IS
    'Whether the storage layer could not say if the conflicting client edit committed; neither value of client_edit_applied is then trustworthy (#2430)';
