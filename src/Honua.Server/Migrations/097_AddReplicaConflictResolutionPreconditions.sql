-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 097_AddReplicaConflictResolutionPreconditions.sql
-- Description: Adds the state a disconnected-sync conflict resolution needs to be safe when it is
--              applied long after the conflict was detected (#2430):
--                * storage_layer_id + resolution_base_generation let a resolution verify the target
--                  feature has not been edited again since the conflict's own batch, so a late
--                  keep-server / merge / geometry-choice / accepted-delete cannot silently overwrite
--                  a legitimate post-conflict edit;
--                * write_committed + finalized let an interrupted resolution be resumed exactly once
--                  instead of leaving the conflict terminally claimed with its produced generation or
--                  audit evidence permanently absent.
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql) and
--               096_AddReplicaConflictClientEditApplied.sql.

-- Storage-layer id of the conflicting feature, as used by honua.feature_changes. Nullable: rows
-- written before this migration have no value, and the resolution surface then skips the staleness
-- precondition rather than blocking on a value it cannot reconstruct.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS storage_layer_id INT;

-- Server generation as of the moment the conflict's own sync batch finished touching its layer. A
-- change to (storage_layer_id, objectid) after this generation is a newer, post-conflict edit.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS resolution_base_generation BIGINT;

-- Whether the resolution's feature write has committed. Set between the write and finalization so a
-- resumed resolution never applies the write twice.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS write_committed BOOLEAN NOT NULL DEFAULT FALSE;

-- Whether the resolution is fully finalized (produced generation persisted, audit evidence written).
-- Existing rows predate the resume path and are complete by definition, so they backfill TRUE; new
-- rows start FALSE and are flipped by the claim/finalize sequence.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS finalized BOOLEAN NOT NULL DEFAULT TRUE;

ALTER TABLE honua.replica_conflicts
    ALTER COLUMN finalized SET DEFAULT FALSE;

-- Resume lookup: find claimed-but-unfinalized resolutions.
CREATE INDEX IF NOT EXISTS idx_replica_conflicts_unfinalized
    ON honua.replica_conflicts(conflict_id)
    WHERE NOT finalized;

COMMENT ON COLUMN honua.replica_conflicts.storage_layer_id IS
    'Storage-layer id used by the change log, for the resolution staleness precondition (#2430)';
COMMENT ON COLUMN honua.replica_conflicts.resolution_base_generation IS
    'Server generation the captured conflict states describe; later changes are post-conflict edits (#2430)';
COMMENT ON COLUMN honua.replica_conflicts.write_committed IS
    'Whether the resolution feature write committed, so a resumed resolution never re-applies it (#2430)';
COMMENT ON COLUMN honua.replica_conflicts.finalized IS
    'Whether the resolution generation and audit evidence are durable; false is resumable (#2430)';
