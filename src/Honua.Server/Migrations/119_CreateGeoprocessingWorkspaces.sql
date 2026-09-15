-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Durable workspace and artifact references for the existing GP env:workspace contract.
-- Referenced output objects retain their owning job/provider's storage lifecycle.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.gp_workspaces (
    workspace_id text PRIMARY KEY,
    kind integer NOT NULL,
    label text COLLATE "C" NOT NULL,
    owner_id text COLLATE "C" NOT NULL,
    scope_id text COLLATE "C" NULL,
    state integer NOT NULL,
    uri text NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NULL
);
CREATE INDEX IF NOT EXISTS ix_gp_workspaces_owner_scope_label
    ON $HonuaSchema$.gp_workspaces(owner_id, scope_id, label);
CREATE INDEX IF NOT EXISTS ix_gp_workspaces_expiry
    ON $HonuaSchema$.gp_workspaces(expires_at) WHERE expires_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.gp_workspace_artifacts (
    artifact_id text PRIMARY KEY,
    workspace_id text NOT NULL REFERENCES $HonuaSchema$.gp_workspaces(workspace_id) ON DELETE RESTRICT,
    kind integer NOT NULL,
    label text NOT NULL,
    label_key text COLLATE "C" NOT NULL,
    state integer NOT NULL,
    uri text NULL,
    content_type text NULL,
    size_bytes bigint NOT NULL CHECK (size_bytes >= 0),
    created_at timestamptz NOT NULL,
    metadata jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_gp_workspace_artifacts_workspace
    ON $HonuaSchema$.gp_workspace_artifacts(workspace_id);
-- Available = 1. The invariant also protects writes made outside the lifecycle service.
CREATE UNIQUE INDEX IF NOT EXISTS ux_gp_workspace_artifacts_available_label
    ON $HonuaSchema$.gp_workspace_artifacts(workspace_id, label_key) WHERE state = 1;
