-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 102_WidenReplicaConflictTypeForDeleteDelete.sql
-- Description: Accepts ReplicaConflictType.DeleteDelete (ordinal 7) (#2430). A client delete colliding
--              with a server delete is now classified explicitly instead of falling through to
--              Attribute, but the original check constraint from 040_AddReplicaConflictReview.sql only
--              permitted 0..6, so every durable delete-vs-delete conflict was rejected by the database:
--              a manual-review synchronization failed while recording it, and last-write-wins lost the
--              review record through the tolerated insert failure.
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql).

ALTER TABLE honua.replica_conflicts
    DROP CONSTRAINT IF EXISTS replica_conflicts_valid_type;

ALTER TABLE honua.replica_conflicts
    ADD CONSTRAINT replica_conflicts_valid_type CHECK(conflict_type BETWEEN 0 AND 7);
