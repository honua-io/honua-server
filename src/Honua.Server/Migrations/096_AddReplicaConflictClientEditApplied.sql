-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 096_AddReplicaConflictClientEditApplied.sql
-- Description: Records whether a disconnected-sync conflict's client edit was still committed to the
--              layer when the conflict was detected. Conflict resolution cannot be planned without it:
--              under last-write-wins the client edit already landed (so "accept client" is a no-op and
--              "keep server" must restore the captured pre-conflict server state), while under manual
--              review the client edit was skipped and the polarity flips (#2430).
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql).

-- Existing rows predate the manual-review conflict-handling mode, which did not exist when they were
-- written: every conflict recorded before this migration was recorded under last-write-wins, so the
-- client edit was applied. Backfilling TRUE (rather than the column default) keeps their resolution
-- semantics correct.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS client_edit_applied BOOLEAN NOT NULL DEFAULT TRUE;

-- New rows carry the value the sync service computed; the default only covers the backfill above.
ALTER TABLE honua.replica_conflicts
    ALTER COLUMN client_edit_applied SET DEFAULT FALSE;

COMMENT ON COLUMN honua.replica_conflicts.client_edit_applied IS
    'Whether the conflicting client edit was still committed to the layer (last-write-wins) or skipped for manual review (#2430)';
