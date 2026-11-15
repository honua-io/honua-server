-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.agent_workspace_identities (
    workspace_id   TEXT COLLATE "C" PRIMARY KEY CHECK (workspace_id ~ '^[0-9a-f]{32}$'),
    owner_actor_id TEXT NOT NULL CHECK (length(owner_actor_id) BETWEEN 1 AND 1024),
    target_id      TEXT NOT NULL CHECK (length(target_id) BETWEEN 1 AND 128),
    tenant_id      TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE $HonuaSchema$.agent_workspace_identities IS 'Immutable human owner, tenant and target for native local process workspace grants.';
