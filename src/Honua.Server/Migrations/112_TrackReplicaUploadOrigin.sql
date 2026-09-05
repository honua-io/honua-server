-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Retain upload provenance without rewriting historical change rows. The default is
-- evaluated by the existing change-tracking INSERT inside the feature transaction.
ALTER TABLE honua.feature_changes ADD COLUMN IF NOT EXISTS origin_replica_id text;
ALTER TABLE honua.feature_changes ALTER COLUMN origin_replica_id
    SET DEFAULT NULLIF(current_setting('honua.origin_replica_id', true), '');

CREATE INDEX IF NOT EXISTS idx_feature_changes_replica_object
    ON honua.feature_changes (origin_replica_id, layer_id, objectid, generation DESC)
    WHERE origin_replica_id IS NOT NULL;
