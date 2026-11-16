-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.feature_edit_locks (
    service_name TEXT NOT NULL,
    layer_id INTEGER NOT NULL,
    feature_id TEXT NOT NULL,
    lock_id TEXT NOT NULL,
    holder_id TEXT NOT NULL,
    display_name TEXT,
    session_id TEXT,
    tenant_id TEXT,
    principal_name TEXT,
    acquired_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    renewed_at TIMESTAMPTZ,
    PRIMARY KEY (service_name, layer_id, feature_id)
);
CREATE INDEX IF NOT EXISTS idx_feature_edit_locks_expires_at
    ON $HonuaSchema$.feature_edit_locks (expires_at);
