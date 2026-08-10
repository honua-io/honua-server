-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 103_AddReplicaConflictPreWriteRowAbsent.sql
-- Description: Records that the conflicting row was absent when a resolution claimed its pre-write
--              state (#2430). A null state token is otherwise ambiguous between a completed absence
--              capture and a pre-write phase that never became durable, so recovery cannot safely
--              re-apply an interrupted expected-absence write without this marker.
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql) and
--               101_AddReplicaConflictPreWriteStateToken.sql.

ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS pre_write_row_absent BOOLEAN;

COMMENT ON COLUMN honua.replica_conflicts.pre_write_row_absent IS
    'Whether the row was absent when the resolution claimed its pre-write state (#2430)';
