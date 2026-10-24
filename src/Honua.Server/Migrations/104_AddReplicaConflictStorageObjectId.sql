-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 104_AddReplicaConflictStorageObjectId.sql
-- Description: Preserves the internal feature id used by the storage change log when a replica
--              addresses the feature through a different public id.primary value (#2430).
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql) and
--               103_AddReplicaConflictPreWriteRowAbsent.sql.

ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS storage_objectid BIGINT;

COMMENT ON COLUMN honua.replica_conflicts.storage_objectid IS
    'Internal feature id recorded by the storage change log; objectid remains the public replica id (#2430)';
