-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 098_AddReplicaConflictClaimIdentity.sql
-- Description: Binds a disconnected-sync conflict resolution to the exact inputs it was claimed with
--              (#2430). Without it, a retry carrying different mergeFields values or a different
--              chooseGeometry side matched the in-flight claim on operator and action alone; because
--              the earlier write had already committed, the service finalized that earlier write while
--              the response and audit described the newly requested — and never applied — state.
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql) and
--               097_AddReplicaConflictResolutionPreconditions.sql.

-- Hash of the normalized resolution inputs (action plus the operator-supplied field values / geometry
-- side) recorded when the claim is taken. Nullable: rows claimed before this column existed have no
-- hash, and a resume then falls back to the operator/action check as before rather than being blocked.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS resolution_input_hash TEXT;

COMMENT ON COLUMN honua.replica_conflicts.resolution_input_hash IS
    'Hash of the resolution inputs the claim was taken with, so a resume cannot finalize a different request (#2430)';
