-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Keep cleanup intent after an uncommitted session expires. An object write can
-- finish after its database connection is lost, including after the first delete.
-- Retain these small tombstones and periodically retry the deterministic object
-- key; deleting a missing object does not prove that an in-flight write has ended.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.attachment_upload_cleanup_ledger (
    session_id UUID PRIMARY KEY,
    layer_id INTEGER NOT NULL CHECK (layer_id >= 0),
    feature_id BIGINT NOT NULL,
    next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_attachment_upload_cleanup_ledger_due
    ON $HonuaSchema$.attachment_upload_cleanup_ledger (next_attempt_at, session_id);
