-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Retain upload provenance without rewriting historical change rows. The default is
-- evaluated by the existing change-tracking INSERT inside the feature transaction.
ALTER TABLE honua.feature_changes ADD COLUMN IF NOT EXISTS origin_replica_id text;
ALTER TABLE honua.feature_changes ALTER COLUMN origin_replica_id
    SET DEFAULT NULLIF(current_setting('honua.origin_replica_id', true), '');
