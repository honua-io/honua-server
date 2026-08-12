-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 101_AddReplicaConflictPreWriteStateToken.sql
-- Description: Stores the optimistic-concurrency token for the conflicting row as it was when a
--              resolution was claimed (#2430). A recovery that must re-apply a write whose
--              write_committed marker never landed uses this token as the write's precondition; a token
--              derived at retry time would describe whatever is in the row now, including a foreign edit
--              that landed during the claim lease, and the write would then overwrite it.
-- Dependencies: Requires honua.replica_conflicts (040_AddReplicaConflictReview.sql) and
--               100_AddReplicaConflictClientEditSuperseded.sql.

-- Nullable: claims taken before this column existed have no token, and recovery for those falls back
-- to the previous behaviour rather than being blocked.
ALTER TABLE honua.replica_conflicts
    ADD COLUMN IF NOT EXISTS pre_write_state_token TEXT;

COMMENT ON COLUMN honua.replica_conflicts.pre_write_state_token IS
    'Row state token captured when the resolution was claimed, used as the precondition for a recovered write (#2430)';
