-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 046_AddReplicaUploadBaseGeneration.sql
-- Description: Adds a per-replica upload base/target generation cursor so a download delta
--              following an upload sync can exclude the client's own just-applied edits. The
--              upload sync advances this cursor to the server generation produced once the
--              uploaded edits have been applied; the subsequent download uses it as the
--              "since" cursor instead of last_sync_generation, which would otherwise replay the
--              client's own edits back to it (#1272).
-- Dependencies: Requires honua.replicas (012_AddReplicationDurability.sql).

ALTER TABLE honua.replicas
    ADD COLUMN IF NOT EXISTS upload_base_generation BIGINT NOT NULL DEFAULT 0;

COMMENT ON COLUMN honua.replicas.upload_base_generation IS
    'Server generation produced by the most recent upload sync; used as the download "since" cursor so a replica does not receive its own just-applied edits back (#1272)';
