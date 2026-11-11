-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Approval and operation control plane (ADR-0082 group A). A row that is active is never swept;
-- expires_at applies once it is terminal.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.operation_proposals (
    proposal_id TEXT COLLATE "C" PRIMARY KEY CHECK (length(proposal_id) BETWEEN 1 AND 512),
    version     BIGINT NOT NULL CHECK (version >= 0),
    kind        TEXT NOT NULL,
    active      BOOLEAN NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL,
    expires_at  TIMESTAMPTZ NOT NULL,
    record      JSON NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_operation_proposals_active
    ON $HonuaSchema$.operation_proposals (kind, updated_at DESC) WHERE active;
CREATE INDEX IF NOT EXISTS idx_operation_proposals_expired
    ON $HonuaSchema$.operation_proposals (expires_at) WHERE NOT active;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.operation_instances (
    operation_instance_id TEXT COLLATE "C" PRIMARY KEY CHECK (length(operation_instance_id) BETWEEN 1 AND 512),
    version               BIGINT NOT NULL CHECK (version >= 0),
    active                BOOLEAN NOT NULL,
    expires_at            TIMESTAMPTZ NOT NULL,
    record                JSON NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_operation_instances_active
    ON $HonuaSchema$.operation_instances (operation_instance_id) WHERE active;
CREATE INDEX IF NOT EXISTS idx_operation_instances_expired
    ON $HonuaSchema$.operation_instances (expires_at) WHERE NOT active;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.operation_secrets (
    reference_id          TEXT COLLATE "C" PRIMARY KEY,
    operation_instance_id TEXT NOT NULL,
    operation_id          TEXT NOT NULL,
    principal_id          TEXT NOT NULL,
    tenant_id             TEXT NOT NULL,
    name                  TEXT NOT NULL,
    protected_value       TEXT NOT NULL,
    expires_at            TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_operation_secrets_expires
    ON $HonuaSchema$.operation_secrets (expires_at);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_operations (
    operation_id        TEXT COLLATE "C" PRIMARY KEY CHECK (length(operation_id) BETWEEN 1 AND 512),
    version             BIGINT NOT NULL CHECK (version > 0),
    kind                TEXT NOT NULL,
    status              TEXT NOT NULL,
    active              BOOLEAN NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL,
    updated_at          TIMESTAMPTZ NOT NULL,
    completed_at        TIMESTAMPTZ,
    deploy_target_id    TEXT,
    metadata_package_id TEXT,
    insert_sequence     BIGINT GENERATED ALWAYS AS IDENTITY,
    expires_at          TIMESTAMPTZ NOT NULL,
    record              JSON NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_workflow_operations_active
    ON $HonuaSchema$.workflow_operations (kind, updated_at DESC) WHERE active;
CREATE INDEX IF NOT EXISTS idx_workflow_operations_created
    ON $HonuaSchema$.workflow_operations (created_at DESC, operation_id DESC);
CREATE INDEX IF NOT EXISTS idx_workflow_operations_kind_created
    ON $HonuaSchema$.workflow_operations (kind, created_at DESC, operation_id DESC);
CREATE INDEX IF NOT EXISTS idx_workflow_operations_deploy_target
    ON $HonuaSchema$.workflow_operations (deploy_target_id, created_at DESC)
    WHERE kind = 'Deploy' AND deploy_target_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_workflow_operations_metadata_package
    ON $HonuaSchema$.workflow_operations (metadata_package_id, insert_sequence DESC)
    WHERE metadata_package_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_workflow_operations_expired
    ON $HonuaSchema$.workflow_operations (expires_at) WHERE NOT active;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.data_protection_keys (
    key_sequence  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    friendly_name TEXT,
    element       TEXT NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE $HonuaSchema$.operation_proposals IS 'Operation proposals awaiting or past a human decision; version and updated_at override their copies in record.';
COMMENT ON TABLE $HonuaSchema$.operation_instances IS 'Operation handles; version overrides its copy in record.';
COMMENT ON TABLE $HonuaSchema$.operation_secrets IS 'Protected one-time operation secrets, deleted by the statement that returns them.';
COMMENT ON TABLE $HonuaSchema$.workflow_operations IS 'Deploy, metadata-release and restore operations; the filter columns are written with record.';
COMMENT ON TABLE $HonuaSchema$.data_protection_keys IS 'ASP.NET data-protection key ring elements, encrypted with the key-ring certificate.';
